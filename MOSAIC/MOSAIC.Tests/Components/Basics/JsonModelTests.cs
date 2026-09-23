using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;

namespace MOSAIC.Tests.Components.Basics;

/// <summary>
/// Known-answer tests for <see cref="JsonModel"/>'s pure static parameter accessors
/// (<see cref="JsonModel.GetString"/>, <see cref="JsonModel.GetInt"/>,
/// <see cref="JsonModel.GetDouble"/>, <see cref="JsonModel.GetBool"/>) and <see cref="JsonModel.Validate"/>.
///
/// The accessors operate over <see cref="JsonElement"/> values (as produced when <see cref="JsonModel.Params"/>
/// is deserialized). Test inputs are built with <c>JsonDocument.Parse("...").RootElement</c>.
/// Coercion rules exercised here, derived directly from the source:
///   GetString:  Number -> raw JSON text; True/False -> "true"/"false"; String -> the string.
///   GetInt:     Number -> TryGetInt32, else (int)GetDouble (truncation); String -> int.TryParse (a
///               fractional string like "3.7" FAILS int.TryParse and yields the default, not 3).
///   GetDouble:  Number -> GetDouble; String -> double.TryParse.
///   GetBool:    True/False literally; Number -> (GetInt32() != 0); String -> bool.TryParse.
///   Non-matching ValueKinds (e.g. Null, or a String for a numeric accessor that won't parse) -> default.
/// </summary>
[TestClass]
public class JsonModelTests
{
    /// <summary>Parses a JSON snippet to the <see cref="JsonElement"/> the accessors expect.</summary>
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement;

    #region GetString

    [DataTestMethod]
    // String element -> the underlying string value.
    [DataRow("\"hello\"", "hello")]
    // Number 42 -> raw JSON text "42".
    [DataRow("42", "42")]
    // Number 3.7 -> raw JSON text "3.7".
    [DataRow("3.7", "3.7")]
    // Boolean true/false -> lowercase literals.
    [DataRow("true", "true")]
    [DataRow("false", "false")]
    public void GetString_JsonElement_CoercesToExpectedText(string json, string expected)
    {
        var element = Element(json);

        var result = JsonModel.GetString(element);

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void GetString_NullElement_ReturnsDefault()
    {
        // A JSON null is ValueKind.Null => the provided default is returned.
        var element = Element("null");

        var result = JsonModel.GetString(element, defaultValue: "fallback");

        Assert.AreEqual("fallback", result);
    }

    [TestMethod]
    public void GetString_NullReference_ReturnsDefault()
    {
        // A literal null object argument short-circuits to the default.
        var result = JsonModel.GetString(null, defaultValue: "fallback");

        Assert.AreEqual("fallback", result);
    }

    #endregion

    #region GetInt

    [DataTestMethod]
    // Integer number parses exactly via TryGetInt32.
    [DataRow("42", 42)]
    // Fractional number cannot TryGetInt32 => (int)GetDouble truncates 3.7 -> 3.
    [DataRow("3.7", 3)]
    // Negative fractional truncates toward zero: (int)(-3.7) = -3.
    [DataRow("-3.7", -3)]
    // Numeric string parses via int.TryParse.
    [DataRow("\"42\"", 42)]
    public void GetInt_JsonElement_CoercesToExpectedInt(string json, int expected)
    {
        var element = Element(json);

        var result = JsonModel.GetInt(element);

        Assert.AreEqual(expected, result); // exact integer arithmetic
    }

    [TestMethod]
    public void GetInt_FractionalString_FailsParseAndReturnsDefault()
    {
        // "3.7" is not a valid Int32 literal => int.TryParse fails => default (not truncated to 3).
        var element = Element("\"3.7\"");

        var result = JsonModel.GetInt(element, defaultValue: -1);

        Assert.AreEqual(-1, result);
    }

    [TestMethod]
    public void GetInt_WrongType_ReturnsDefault()
    {
        // A boolean element is neither Number nor String => the default is returned.
        var element = Element("true");

        var result = JsonModel.GetInt(element, defaultValue: 99);

        Assert.AreEqual(99, result);
    }

    #endregion

    #region GetDouble

    [DataTestMethod]
    // Integer widens to double.
    [DataRow("42", 42.0)]
    // Fractional number preserved.
    [DataRow("3.7", 3.7)]
    // Numeric string parses via double.TryParse.
    [DataRow("\"2.5\"", 2.5)]
    public void GetDouble_JsonElement_CoercesToExpectedDouble(string json, double expected)
    {
        var element = Element(json);

        var result = JsonModel.GetDouble(element);

        Assert.AreEqual(expected, result, 1e-9); // exact decimal values
    }

    [TestMethod]
    public void GetDouble_NonNumericString_ReturnsDefault()
    {
        // "abc" fails double.TryParse => the provided default is returned.
        var element = Element("\"abc\"");

        var result = JsonModel.GetDouble(element, defaultValue: 1.5);

        Assert.AreEqual(1.5, result, 1e-9);
    }

    [TestMethod]
    public void GetDouble_WrongType_ReturnsDefault()
    {
        // A boolean element is neither Number nor String => the default is returned.
        var element = Element("false");

        var result = JsonModel.GetDouble(element, defaultValue: 7.25);

        Assert.AreEqual(7.25, result, 1e-9);
    }

    #endregion

    #region GetBool

    [DataTestMethod]
    // Boolean literals map directly.
    [DataRow("true", true)]
    [DataRow("false", false)]
    // Number: (GetInt32() != 0). 0 -> false, non-zero -> true.
    [DataRow("0", false)]
    [DataRow("5", true)]
    // String: bool.TryParse (case-insensitive) succeeds for "true"/"false".
    [DataRow("\"true\"", true)]
    [DataRow("\"False\"", false)]
    public void GetBool_JsonElement_CoercesToExpectedBool(string json, bool expected)
    {
        var element = Element(json);

        var result = JsonModel.GetBool(element);

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void GetBool_UnparsableString_ReturnsDefault()
    {
        // "yes" is not a valid Boolean literal => bool.TryParse fails => default.
        var element = Element("\"yes\"");

        var result = JsonModel.GetBool(element, defaultValue: true);

        Assert.AreEqual(true, result);
    }

    [TestMethod]
    public void GetBool_NullElement_ReturnsDefault()
    {
        // ValueKind.Null matches no arm => the provided default is returned.
        var element = Element("null");

        var result = JsonModel.GetBool(element, defaultValue: true);

        Assert.AreEqual(true, result);
    }

    #endregion

    #region Validate

    [TestMethod]
    public void Validate_ValidModel_DoesNotThrow()
    {
        // Non-empty Type and non-negative DesiredRate => Validate completes without throwing.
        var model = new JsonModel { Type = "Blocks.Filters.Rectifier", DesiredRate = 200 };

        model.Validate();
    }

    [TestMethod]
    public void Validate_NegativeDesiredRate_Throws()
    {
        // DesiredRate < 0 violates the runtime guard => ValidationException.
        var model = new JsonModel { Type = "Blocks.Filters.Rectifier", DesiredRate = -1 };

        Assert.ThrowsExactly<ValidationException>(() => model.Validate());
    }

    [TestMethod]
    public void Validate_WhitespaceType_Throws()
    {
        // Type that is whitespace-only is treated as missing => ValidationException.
        var model = new JsonModel { Type = "   " };

        Assert.ThrowsExactly<ValidationException>(() => model.Validate());
    }

    #endregion
}
