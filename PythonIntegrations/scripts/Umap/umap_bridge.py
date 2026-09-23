"""
UMAP bridge for MOSAIC — headless, no GUI dependencies.

Provides a UmapModel class that wraps umap-learn for:
  - fit(X, y) on labelled calibration data
  - transform(X) for online projection of new points
  - save/load for model persistence

Called from C# via Python.NET. All arrays are numpy ndarrays.

Usage from C#:
    dynamic bridge = Py.Import("umap_bridge");
    dynamic model  = bridge.UmapModel(n_components=3, n_neighbors=15, min_dist=0.1);
    model.fit(X_np, y_np);
    embedding = model.transform(x_new_np);
"""

import numpy as np
import pickle
import os


class UmapModel:
    """Thin wrapper around umap.UMAP with fit/transform/persistence."""

    def __init__(
        self,
        n_components: int = 3,
        n_neighbors: int = 15,
        min_dist: float = 0.1,
        metric: str = "euclidean",
        random_state: int = 42,
    ):
        import umap

        self.n_components = n_components
        self.n_neighbors = n_neighbors
        self.min_dist = min_dist
        self.metric = metric
        self.random_state = random_state

        self._umap = umap.UMAP(
            n_components=n_components,
            n_neighbors=n_neighbors,
            min_dist=min_dist,
            metric=metric,
            random_state=random_state,
        )
        self._is_fitted = False
        self._train_embedding = None  # stored after fit for calibration display
        self._train_labels = None

    @property
    def is_fitted(self) -> bool:
        return self._is_fitted

    def fit(self, X: np.ndarray, y: np.ndarray = None) -> np.ndarray:
        """
        Fit the UMAP model on calibration data.

        Parameters
        ----------
        X : ndarray of shape (n_samples, n_features)
            Feature matrix (float64).
        y : ndarray of shape (n_samples,), optional
            Integer class labels. Passed to UMAP for supervised embedding.

        Returns
        -------
        embedding : ndarray of shape (n_samples, n_components)
            The low-dimensional embedding of the training data.
        """
        X = np.asarray(X, dtype=np.float64)
        if y is not None:
            y = np.asarray(y, dtype=np.int64)

        self._train_embedding = self._umap.fit_transform(X, y=y)
        self._train_labels = y
        self._is_fitted = True

        return self._train_embedding

    def transform(self, X: np.ndarray) -> np.ndarray:
        """
        Project new point(s) into the fitted UMAP space.

        Parameters
        ----------
        X : ndarray of shape (n_features,) or (n_samples, n_features)
            One or more feature vectors.

        Returns
        -------
        embedding : ndarray of shape (n_samples, n_components)
            Projected coordinates. For a single input vector, shape is (1, n_components).
        """
        if not self._is_fitted:
            raise RuntimeError("Model not fitted. Call fit() first.")

        X = np.asarray(X, dtype=np.float64)
        if X.ndim == 1:
            X = X.reshape(1, -1)

        return self._umap.transform(X)

    def get_train_embedding(self) -> np.ndarray:
        """Return the embedding of the training data (computed at fit time)."""
        if self._train_embedding is None:
            raise RuntimeError("No training embedding available. Call fit() first.")
        return self._train_embedding

    def get_train_labels(self) -> np.ndarray:
        """Return the training labels (if provided at fit time)."""
        return self._train_labels

    def save(self, path: str) -> None:
        """Pickle the fitted UMAP model to disk."""
        if not self._is_fitted:
            raise RuntimeError("Cannot save unfitted model.")
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        with open(path, "wb") as f:
            pickle.dump(
                {
                    "umap": self._umap,
                    "train_embedding": self._train_embedding,
                    "train_labels": self._train_labels,
                    "params": {
                        "n_components": self.n_components,
                        "n_neighbors": self.n_neighbors,
                        "min_dist": self.min_dist,
                        "metric": self.metric,
                        "random_state": self.random_state,
                    },
                },
                f,
            )

    def load(self, path: str) -> None:
        """Load a trusted UMAP model previously saved by this class.

        Pickle data can execute arbitrary code while loading. Never pass a
        model downloaded from an untrusted or unauthenticated source.
        """
        with open(path, "rb") as f:
            data = pickle.load(f)
        self._umap = data["umap"]
        self._train_embedding = data["train_embedding"]
        self._train_labels = data["train_labels"]
        self._is_fitted = True

        params = data.get("params", {})
        self.n_components = params.get("n_components", self.n_components)
        self.n_neighbors = params.get("n_neighbors", self.n_neighbors)
        self.min_dist = params.get("min_dist", self.min_dist)
        self.metric = params.get("metric", self.metric)
        self.random_state = params.get("random_state", self.random_state)
