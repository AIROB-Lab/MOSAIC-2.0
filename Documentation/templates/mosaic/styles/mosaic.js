(() => {
  "use strict";
  const rel = document.querySelector('meta[name="docfx:rel"]').content || "./";
  const root = new URL(rel, location.href);
  const local = path => new URL(path, root).href;
  const article = document.querySelector("#_content");
  // Keep teaching recordings still until requested; links also work without JS.
  const stopDemos = [];
  article.querySelectorAll(".workbench-demo").forEach(figure => {
    const img = figure.querySelector("img");
    const link = figure.querySelector(".demo-animation");
    if (!img || !link) return;
    const poster = img.src;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "demo-toggle";
    function stop() {
      img.src = poster;
      button.textContent = "Play animation";
      button.setAttribute("aria-pressed", "false");
    }
    stop();
    stopDemos.push(stop);
    button.addEventListener("click", () => {
      if (button.getAttribute("aria-pressed") === "true") { stop(); return; }
      stopDemos.forEach(reset => reset());
      img.src = link.href;
      button.textContent = "Stop animation";
      button.setAttribute("aria-pressed", "true");
    });
    img.addEventListener("error", () => {
      if (button.getAttribute("aria-pressed") === "true") stop();
    });
    link.before(button);
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) stopDemos.forEach(stop => stop());
  });
  window.addEventListener("beforeprint", () => stopDemos.forEach(stop => stop()));
  const isHome = !!article.querySelector(".docs-home");
  const isApi = location.pathname.startsWith(new URL("api/", root).pathname);
  const isCatalogue = location.pathname.endsWith("/block-catalogue.html") || location.pathname.startsWith(new URL("docs/blocks/", root).pathname);
  document.body.classList.toggle("is-home", isHome);
  document.body.classList.toggle("is-api", isApi);
  document.querySelector("#page-section").textContent = isApi ? "API reference" : isCatalogue ? "Block reference" : "Guides";
  const samePage = href => new URL(href, location.href).pathname.replace(/index\.html$/, "") === location.pathname.replace(/index\.html$/, "");
  function markActive(container) {
    container.querySelectorAll("a[href]").forEach(a => {
      if (samePage(a.href)) { a.classList.add("active"); a.setAttribute("aria-current", "page"); }
    });
  }
  markActive(document.querySelector(".sidebar"));
  const headerLinks = document.querySelectorAll(".header-links a");
  if (isApi) headerLinks[2].classList.add("active");
  else if (!isHome) headerLinks[isCatalogue ? 1 : 0].classList.add("active");

  function makeLink(label, href) {
    const a = document.createElement("a");
    a.textContent = label;
    a.href = href;
    return a;
  }
  async function loadGuides() {
    const response = await fetch(local("docs/toc.json"));
    if (!response.ok) throw new Error("Navigation unavailable");
    const data = await response.json();
    const nav = document.querySelector("#guide-nav");
    const fragment = document.createDocumentFragment();
    function addItems(items, parent) {
      for (const item of items || []) {
        const href = item.topicHref || item.href;
        if (href) {
          parent.append(makeLink(item.name, new URL(href, local("docs/")).href));
          if (item.items) addItems(item.items, parent);
        } else {
          const group = document.createElement("details");
          group.className = "guide-group";
          const heading = document.createElement("summary");
          heading.textContent = item.name;
          const links = document.createElement("div");
          links.className = "guide-group-links";
          addItems(item.items, links);
          group.open = [...links.querySelectorAll("a")].some(a => samePage(a.href) ||
            (isCatalogue && a.href.endsWith("/block-catalogue.html")));
          if (isHome && !parent.children.length) group.open = true;
          group.append(heading, links);
          parent.append(group);
        }
      }
    }
    addItems(data.items, fragment);
    nav.replaceChildren(fragment);
    markActive(nav);
    if (isCatalogue) nav.querySelector('a[href$="/block-catalogue.html"]')?.classList.add("active");
  }
  loadGuides().catch(() => { /* The essential guide links remain available offline. */ });

  async function loadApi() {
    document.querySelector("#guide-nav").hidden = true;
    document.querySelector("#api-nav").hidden = false;
    const [response, blockResponse] = await Promise.all([
      fetch(local("api/toc.json")),
      fetch(local("api-blocks.json")).catch(() => null)
    ]);
    if (!response.ok) throw new Error("API navigation unavailable");
    const data = await response.json();
    const blockData = blockResponse?.ok ? await blockResponse.json() : { blocks: [] };
    const blocks = blockData.blocks || [];
    const tree = document.querySelector("#api-tree");
    const areas = [
      "Core and runtime", "Processing helpers", "Plots and visualization",
      "Learning algorithms", "Devices and connections", "View models",
      "Views and controls", "External libraries"
    ];
    function areaFor(name) {
      if (!/^MOSAIC(?:\.|$)/.test(name)) return 7;
      if (name.startsWith("MOSAIC.Models") || name === "MOSAIC.Components.SignalProcessing") return 1;
      if (name.startsWith("MOSAIC.Visualization")) return 2;
      if (name.startsWith("MOSAIC.MachineLearning") || name.startsWith("MOSAIC.Components.MachineLearning") || name === "MOSAIC.Components.Manager.Python") return 3;
      if (/^MOSAIC\.Components\.(Devices\.|BodyRig$|Manager\.(BLE|Serial)(\.|$))/.test(name)) return 4;
      if (name.startsWith("MOSAIC.ViewModels")) return 5;
      if (/^MOSAIC\.(Views|Controls|Converters|Selector)(\.|$)/.test(name)) return 6;
      return 0;
    }
    const labels = {
      "MOSAIC": "Application", "MOSAIC.Components.Basics": "Block contracts and data",
      "MOSAIC.Components.Factory": "Block creation and catalogue",
      "MOSAIC.Models": "Common block types", "MOSAIC.ViewModels": "Common view models",
      "MOSAIC.Views": "Workbench views", "MOSAIC.Visualization": "Visualization infrastructure",
      "MOSAIC.Components.SignalProcessing": "Signal processing utilities"
    };
    const namespaceLabel = name => labels[name] || name.replace(/^MOSAIC\.(?:Components\.|Models\.|ViewModels\.|Visualization\.)?/, "");
    const compare = (a, b) => a.localeCompare(b, "en", { numeric: true, sensitivity: "base" });
    const namespaces = [];
    const sections = [];
    const apiItems = new Map((data.items || []).flatMap(g => (g.items || []).map(t => [t.topicUid, t])));
    const assigned = new Set();
    const currentBlock = blocks.find(b => (b.model || []).includes(article.dataset.uid));
    const blockGuide = article.querySelector(".model-block-guide");
    if (blockGuide && currentBlock?.guide) {
      blockGuide.href = local(currentBlock.guide);
      blockGuide.textContent = currentBlock.name + ": parameters and connection rules";
    }
    const roots = [];
    function makeRoot(label) {
      const element = document.createElement("details");
      element.className = "api-root";
      const summary = document.createElement("summary");
      summary.textContent = label;
      element.append(summary);
      tree.append(element);
      roots.push({ element, initiallyOpen: false });
      return element;
    }
    const blockRoot = makeRoot("Blocks");
    const sharedRoot = makeRoot("Shared APIs");
    const roles = [["model", "Model"], ["viewModel", "View model"], ["view", "View"], ["related", "Related type"]];
    const categoryOrder = ["Signal Processing", "Flow Control", "Analytics", "Machine Learning", "Devices", "Streaming", "Tests"];
    const categories = [...new Set(blocks.map(b => b.category))].sort((a, b) =>
      (categoryOrder.includes(a) ? categoryOrder.indexOf(a) : 99) - (categoryOrder.includes(b) ? categoryOrder.indexOf(b) : 99) || compare(a, b));
    for (const category of categories) {
      const section = document.createElement("details");
      section.className = "api-area";
      const title = document.createElement("summary");
      title.textContent = category;
      section.append(title);
      for (const block of blocks.filter(b => b.category === category).sort((a, b) => compare(a.name, b.name))) {
        const details = document.createElement("details");
        details.className = "api-namespace api-block";
        details.dataset.block = block.id;
        const summary = document.createElement("summary");
        summary.textContent = block.name;
        details.append(summary);
        const body = document.createElement("div");
        body.className = "api-types";
        const overview = makeLink(block.guide ? "Block guide" : "Block catalogue",
          local(block.guide || "docs/block-catalogue.html"));
        body.append(overview);
        const types = [];
        const related = document.createElement("details");
        related.className = "api-related";
        const relatedTitle = document.createElement("summary");
        relatedTitle.textContent = "Related types";
        related.append(relatedTitle);
        const blockSearch = (category + " " + block.name + " " + (block.aliases || []).join(" ")).toLowerCase();
        for (const [key, label] of roles) {
          const items = (block[key] || []).map(uid => apiItems.get(uid)).filter(Boolean).sort((a, b) => compare(a.name, b.name));
          for (const item of items) {
            const a = makeLink("", new URL(item.topicHref || item.href, local("api/")).href);
            const badge = document.createElement("span");
            badge.className = "api-role";
            badge.textContent = label + " ";
            a.append(badge, document.createTextNode(item.name));
            a.title = item.topicUid;
            a.dataset.uid = item.topicUid;
            (key === "related" ? related : body).append(a);
            assigned.add(item.topicUid);
            types.push({ link: a, search: blockSearch + " " + label.toLowerCase() + " " + item.topicUid.toLowerCase() + " " + item.name.toLowerCase() });
          }
        }
        related.open = [...related.querySelectorAll("a")].some(a => samePage(a.href));
        const relatedActive = related.open;
        if (related.querySelector("a")) body.append(related);
        details.append(body);
        details.open = types.some(t => samePage(t.link.href));
        section.append(details);
        namespaces.push({ details, section, overview, types, related, relatedActive, search: blockSearch, active: details.open });
      }
      section.open = !!section.querySelector("details[open]");
      sections.push({ element: section, initiallyOpen: section.open });
      blockRoot.append(section);
    }
    for (const [index, label] of areas.entries()) {
      const groups = (data.items || []).filter(g => areaFor(g.name) === index)
        .map(g => ({ ...g, items: (g.items || []).filter(t => !assigned.has(t.topicUid)) }))
        .filter(g => g.items.length || samePage(new URL(g.topicHref || g.href, local("api/")).href));
      if (!groups.length) continue;
      groups.sort((a, b) => {
        const coreOrder = ["MOSAIC.Components.Basics", "MOSAIC.Components.Factory"];
        const priority = name => coreOrder.includes(name) ? coreOrder.indexOf(name) : 2;
        return (index === 0 ? priority(a.name) - priority(b.name) : 0) || compare(namespaceLabel(a.name), namespaceLabel(b.name));
      });
      const section = document.createElement("details");
      section.className = "api-area";
      const title = document.createElement("summary");
      title.textContent = label;
      section.append(title);
      for (const group of groups) {
        const details = document.createElement("details");
        details.className = "api-namespace";
        const summary = document.createElement("summary");
        summary.textContent = namespaceLabel(group.name);
        summary.title = group.name;
        details.append(summary);
        const body = document.createElement("div");
        body.className = "api-types";
        const overview = makeLink("Namespace overview", new URL(group.topicHref || group.href, local("api/")).href);
        overview.title = group.name;
        body.append(overview);
        const types = [...(group.items || [])].sort((a, b) => compare(a.name, b.name)).map(item => {
          const a = makeLink(item.name, new URL(item.topicHref || item.href, local("api/")).href);
          a.title = item.topicUid;
          a.dataset.uid = item.topicUid;
          body.append(a);
          return { link: a, search: (label + " " + group.name + " " + namespaceLabel(group.name) + " " + item.name).toLowerCase() };
        });
        details.append(body);
        details.open = [overview, ...types.map(t => t.link)].some(a => samePage(a.href));
        section.append(details);
        namespaces.push({ details, section, overview, types, search: (label + " " + group.name + " " + namespaceLabel(group.name)).toLowerCase(), active: details.open });
      }
      section.open = !!section.querySelector("details[open]");
      sections.push({ element: section, initiallyOpen: section.open });
      sharedRoot.append(section);
    }
    sharedRoot.open = !!sharedRoot.querySelector(".api-namespace[open]") || !blocks.length;
    blockRoot.open = !!blockRoot.querySelector(".api-block[open]") || !sharedRoot.open;
    blockRoot.hidden = !blocks.length;
    roots.forEach(root => { root.initiallyOpen = root.element.open; });
    markActive(tree);
    const status = document.querySelector("#api-filter-status");
    const total = apiItems.size;
    const defaultStatus = blocks.length ? blocks.length + " blocks · " + total + " API types" : "Block grouping unavailable · " + total + " types in Shared APIs";
    status.textContent = defaultStatus;
    document.querySelector("#api-filter").addEventListener("input", event => {
      const query = event.target.value.trim().toLowerCase();
      const words = query.split(/\s+/).filter(Boolean);
      const matched = new Set();
      let foundNamespace = false;
      for (const group of namespaces) {
        const namespaceMatches = words.every(word => group.search.includes(word));
        let matches = 0;
        for (const type of group.types) {
          type.link.hidden = !words.every(word => type.search.includes(word));
          if (!type.link.hidden) { matches++; matched.add(type.link.dataset.uid); }
        }
        group.details.hidden = !namespaceMatches && matches === 0;
        foundNamespace ||= !group.details.hidden;
        group.overview.hidden = group.details.hidden;
        group.details.open = query ? !group.details.hidden : group.active;
        if (group.related) {
          group.related.hidden = ![...group.related.querySelectorAll("a")].some(a => !a.hidden);
          group.related.open = query ? !group.related.hidden : group.relatedActive;
        }
      }
      for (const section of sections) {
        section.element.hidden = !namespaces.some(g => g.section === section.element && !g.details.hidden);
        section.element.open = query ? !section.element.hidden : section.initiallyOpen;
      }
      for (const root of roots) {
        root.element.hidden = ![...root.element.querySelectorAll(".api-area")].some(s => !s.hidden);
        root.element.open = query ? !root.element.hidden : root.initiallyOpen;
      }
      const count = matched.size;
      status.textContent = !query ? defaultStatus : count ?
        count + (count === 1 ? " matching type" : " matching types") :
        foundNamespace ? "Matching block or namespace; no types listed." : "No matches. Try a block, type or namespace such as Filter or Scope.";
    });
  }
  if (isApi) loadApi().catch(() => {
    document.querySelector("#api-tree").textContent = "API navigation could not load. Use search or reload the page.";
  });

  const outline = document.querySelector("#page-outline-links");
  const headings = [...article.querySelectorAll(isApi ? "h2[id], h3[id]" : "h2[id]")];
  if (!headings.length) document.querySelector(".page-outline").hidden = true;
  for (const heading of headings) outline.append(makeLink(heading.textContent, "#" + heading.id));
  if (headings.length && "IntersectionObserver" in window) {
    // Track every section, using one observer for the full outline.
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) if (entry.isIntersecting)
        outline.querySelectorAll("a").forEach(a => a.classList.toggle("current", a.hash === "#" + entry.target.id));
    }, { rootMargin: "-90px 0px -65% 0px" });
    headings.forEach(h => observer.observe(h));
  }
  article.querySelectorAll("table").forEach(table => {
    const wrapper = document.createElement("div");
    wrapper.className = "table-scroll";
    wrapper.tabIndex = 0;
    wrapper.setAttribute("role", "region");
    wrapper.setAttribute("aria-label", "Scrollable table");
    table.before(wrapper);
    wrapper.append(table);
  });
  article.querySelectorAll("pre").forEach(pre => {
    const code = pre.querySelector("code");
    if (!code) return;
    const declaredLanguage = [...code.classList].find(name => name.startsWith("lang-") || name.startsWith("language-"));
    const plain = !declaredLanguage || /^(lang|language)-(text|plaintext|none)$/.test(declaredLanguage);
    if (window.hljs && !plain) {
      try { window.hljs.highlightBlock(code); } catch { /* Keep unsupported languages readable. */ }
    }
    const label = document.createElement("span");
    label.className = "code-language";
    label.textContent = declaredLanguage ? declaredLanguage.replace(/^(lang|language)-/, "") : "Text";
    const copy = document.createElement("button");
    copy.type = "button";
    copy.className = "copy-code";
    copy.textContent = "Copy";
    copy.setAttribute("aria-label", "Copy code example");
    copy.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(code.textContent);
        copy.textContent = "Copied";
      } catch { copy.textContent = "Select to copy"; }
      setTimeout(() => { copy.textContent = "Copy"; }, 1800);
    });
    pre.append(label, copy);
  });

  const catalogueFilter = document.querySelector("#catalogue-filter");
  if (catalogueFilter) {
    const rows = [...article.querySelectorAll("tr")].filter(row => row.querySelector("span[id]"));
    const tables = [...new Set(rows.map(row => row.closest("table")))];
    const status = document.querySelector("#catalogue-filter-status");
    function filterCatalogue() {
      const words = catalogueFilter.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
      let count = 0;
      for (const row of rows) {
        row.hidden = !words.every(word => row.textContent.toLowerCase().includes(word));
        if (!row.hidden) count++;
      }
      for (const table of tables) {
        const empty = ![...table.querySelectorAll("tr")].some(row => row.querySelector("span[id]") && !row.hidden);
        const wrapper = table.closest(".table-scroll");
        wrapper.hidden = empty;
        if (wrapper.previousElementSibling?.tagName === "H2") {
          const heading = wrapper.previousElementSibling;
          heading.hidden = empty;
          const outlineLink = outline.querySelector(`a[href="#${heading.id}"]`);
          if (outlineLink) outlineLink.hidden = empty;
        }
      }
      status.textContent = count ? `${count} of ${rows.length} blocks shown` : "No matching blocks. Try another name or purpose.";
    }
    catalogueFilter.addEventListener("input", filterCatalogue);
    filterCatalogue();
  }

  article.querySelectorAll(".inheritance, .inheritedMembers, .implements").forEach(group => {
    const heading = group.querySelector("h5");
    if (!heading) return;
    const details = document.createElement("details");
    details.className = "api-relations";
    const summary = document.createElement("summary");
    summary.textContent = heading.textContent + " · " + group.querySelectorAll(":scope > div").length;
    heading.remove();
    group.before(details);
    details.append(summary, group);
  });

  const menu = document.querySelector(".menu-button");
  const backdrop = document.querySelector(".mobile-backdrop");
  const sidebar = document.querySelector("#sidebar");
  const mobileWidth = matchMedia("(max-width:720px)");
  sidebar.inert = mobileWidth.matches;
  function closeMenu() {
    document.body.classList.remove("nav-open");
    menu.setAttribute("aria-expanded", "false");
    menu.setAttribute("aria-label", "Open navigation");
    backdrop.hidden = true;
    sidebar.inert = mobileWidth.matches;
  }
  menu.addEventListener("click", () => {
    const open = !document.body.classList.contains("nav-open");
    document.body.classList.toggle("nav-open", open);
    menu.setAttribute("aria-expanded", String(open));
    menu.setAttribute("aria-label", open ? "Close navigation" : "Open navigation");
    backdrop.hidden = !open;
    sidebar.inert = !open && mobileWidth.matches;
  });
  backdrop.addEventListener("click", closeMenu);
  document.addEventListener("keydown", e => { if (e.key === "Escape") closeMenu(); });
  mobileWidth.addEventListener("change", closeMenu);

  const dialog = document.querySelector("#search-dialog");
  const search = document.querySelector("#search-input");
  const status = document.querySelector("#search-status");
  const results = document.querySelector("#search-results");
  let searchData;
  let requestId = 0;
  async function getIndex() {
    if (!searchData) {
      searchData = fetch(local("index.json")).then(response => {
        if (!response.ok) throw new Error("Search unavailable");
        return response.json();
      }).then(data => Object.values(data).map(item => ({
        ...item,
        title: item.title.replace(/\s*\| MOSAIC\s*$/, ""),
        text: (item.title + " " + item.summary).toLowerCase()
      }))).catch(error => { searchData = null; throw error; });
    }
    return searchData;
  }
  function openSearch() {
    closeMenu();
    dialog.showModal();
    search.focus();
  }
  document.querySelector(".search-trigger").addEventListener("click", openSearch);
  document.querySelector(".search-close").addEventListener("click", () => dialog.close());
  document.addEventListener("keydown", e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
      e.preventDefault();
      if (!dialog.open) openSearch();
    }
  });
  search.addEventListener("input", async () => {
    const id = ++requestId;
    const query = search.value.trim().toLowerCase();
    results.replaceChildren();
    if (!query) { status.textContent = "Start typing to search the documentation."; return; }
    status.textContent = "Searching…";
    try {
      const index = await getIndex();
      if (id !== requestId) return;
      const words = query.split(/\s+/);
      const matches = index.filter(item => words.every(word => item.text.includes(word)))
        .map(item => ({ item, score: (item.href.startsWith("docs/") ? 15 : 0) +
          (item.title.toLowerCase().includes(query) ? 100 : 0) +
          words.filter(word => item.title.toLowerCase().includes(word)).length * 10 }))
        .sort((a, b) => b.score - a.score);
      status.textContent = matches.length ? matches.length + " results · showing " + Math.min(matches.length, 20) : "No results. Try a block name, topic, or API type.";
      for (const {item} of matches.slice(0, 20)) {
        const a = makeLink("", local(item.href));
        const title = document.createElement("strong");
        title.textContent = item.title;
        const excerpt = document.createElement("span");
        const summary = item.summary || "";
        const hit = summary.toLowerCase().indexOf(words[0]);
        const start = Math.max(0, hit - 45);
        excerpt.textContent = (start ? "…" : "") + summary.slice(start, start + 160) + (summary.length > start + 160 ? "…" : "");
        a.append(title, excerpt);
        results.append(a);
      }
    } catch { if (id === requestId) status.textContent = "Search could not load. Please reload the page and try again."; }
  });
})();
