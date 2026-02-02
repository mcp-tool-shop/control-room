using System.Text.RegularExpressions;

namespace ControlRoom.Application.Services;

/// <summary>
/// Service for friendly, user-correctable validation with inline error messages.
/// Preserves user input and provides specific, actionable feedback.
/// </summary>
public sealed partial class ValidationService
{
    private readonly Dictionary<string, List<ValidationMessage>> _fieldErrors = new();
    private readonly Dictionary<string, object?> _fieldValues = new();

    /// <summary>
    /// Event fired when validation state changes.
    /// </summary>
    public event EventHandler<ValidationChangedEventArgs>? ValidationChanged;

    /// <summary>
    /// Gets whether all fields are currently valid.
    /// </summary>
    public bool IsValid => !_fieldErrors.Values.Any(errors => errors.Count > 0);

    /// <summary>
    /// Gets all current validation errors.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ValidationMessage>> Errors =>
        _fieldErrors.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<ValidationMessage>)kvp.Value.AsReadOnly());

    /// <summary>
    /// Gets the first invalid field name, or null if all valid.
    /// </summary>
    public string? FirstInvalidField =>
        _fieldErrors.FirstOrDefault(kvp => kvp.Value.Count > 0).Key;

    /// <summary>
    /// Validates a required field.
    /// </summary>
    public ValidationResult ValidateRequired(string field, object? value, string? friendlyName = null)
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        var isEmpty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            IEnumerable<object> e => !e.Any(),
            _ => false
        };

        if (isEmpty)
        {
            return SetError(field, new ValidationMessage(
                $"{name} is required",
                ValidationSeverity.Error,
                "required"));
        }

        return ClearError(field, "required");
    }

    /// <summary>
    /// Validates a string length.
    /// </summary>
    public ValidationResult ValidateLength(
        string field,
        string? value,
        int? minLength = null,
        int? maxLength = null,
        string? friendlyName = null)
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        var length = value?.Length ?? 0;

        if (minLength.HasValue && length < minLength.Value)
        {
            return SetError(field, new ValidationMessage(
                $"{name} must be at least {minLength.Value} character{(minLength.Value != 1 ? "s" : "")}",
                ValidationSeverity.Error,
                "minLength"));
        }

        if (maxLength.HasValue && length > maxLength.Value)
        {
            return SetError(field, new ValidationMessage(
                $"{name} must be no more than {maxLength.Value} character{(maxLength.Value != 1 ? "s" : "")}",
                ValidationSeverity.Error,
                "maxLength"));
        }

        ClearError(field, "minLength");
        return ClearError(field, "maxLength");
    }

    /// <summary>
    /// Validates a value is within a range.
    /// </summary>
    public ValidationResult ValidateRange<T>(
        string field,
        T? value,
        T? min = default,
        T? max = default,
        string? friendlyName = null) where T : struct, IComparable<T>
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        if (!value.HasValue)
        {
            return ClearError(field, "range");
        }

        if (min.HasValue && value.Value.CompareTo(min.Value) < 0)
        {
            return SetError(field, new ValidationMessage(
                $"{name} must be at least {min.Value}",
                ValidationSeverity.Error,
                "range"));
        }

        if (max.HasValue && value.Value.CompareTo(max.Value) > 0)
        {
            return SetError(field, new ValidationMessage(
                $"{name} must be no more than {max.Value}",
                ValidationSeverity.Error,
                "range"));
        }

        return ClearError(field, "range");
    }

    /// <summary>
    /// Validates an email address format.
    /// </summary>
    public ValidationResult ValidateEmail(string field, string? value, string? friendlyName = null)
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return ClearError(field, "email");
        }

        if (!EmailRegex().IsMatch(value))
        {
            return SetError(field, new ValidationMessage(
                $"{name} must be a valid email address",
                ValidationSeverity.Error,
                "email"));
        }

        return ClearError(field, "email");
    }

    /// <summary>
    /// Validates a URL format.
    /// </summary>
    public ValidationResult ValidateUrl(
        string field,
        string? value,
        bool requireHttps = false,
        string? friendlyName = null)
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return ClearError(field, "url");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return SetError(field, new ValidationMessage(
                $"{name} must be a valid URL",
                ValidationSeverity.Error,
                "url"));
        }

        if (requireHttps && uri.Scheme != "https")
        {
            return SetError(field, new ValidationMessage(
                $"{name} must use HTTPS",
                ValidationSeverity.Error,
                "url"));
        }

        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            return SetError(field, new ValidationMessage(
                $"{name} must be an HTTP or HTTPS URL",
                ValidationSeverity.Error,
                "url"));
        }

        return ClearError(field, "url");
    }

    /// <summary>
    /// Validates against a regular expression pattern.
    /// </summary>
    public ValidationResult ValidatePattern(
        string field,
        string? value,
        string pattern,
        string errorMessage,
        string? friendlyName = null)
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return ClearError(field, "pattern");
        }

        if (!Regex.IsMatch(value, pattern))
        {
            return SetError(field, new ValidationMessage(
                errorMessage.Replace("{field}", name),
                ValidationSeverity.Error,
                "pattern"));
        }

        return ClearError(field, "pattern");
    }

    /// <summary>
    /// Validates a unique name/identifier.
    /// </summary>
    public ValidationResult ValidateIdentifier(
        string field,
        string? value,
        string? friendlyName = null)
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return ClearError(field, "identifier");
        }

        if (!IdentifierRegex().IsMatch(value))
        {
            return SetError(field, new ValidationMessage(
                $"{name} can only contain letters, numbers, hyphens, and underscores",
                ValidationSeverity.Error,
                "identifier"));
        }

        if (char.IsDigit(value[0]))
        {
            return SetError(field, new ValidationMessage(
                $"{name} cannot start with a number",
                ValidationSeverity.Error,
                "identifier"));
        }

        return ClearError(field, "identifier");
    }

    /// <summary>
    /// Validates a cron expression.
    /// </summary>
    public ValidationResult ValidateCronExpression(
        string field,
        string? value,
        string? friendlyName = null)
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return ClearError(field, "cron");
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts.Length > 6)
        {
            return SetError(field, new ValidationMessage(
                $"{name} must have 5 or 6 parts (minute, hour, day, month, weekday, [year])",
                ValidationSeverity.Error,
                "cron"));
        }

        // Basic validation - a full cron parser would be more thorough
        return ClearError(field, "cron");
    }

    /// <summary>
    /// Validates two fields match (for confirmations).
    /// </summary>
    public ValidationResult ValidateMatch(
        string field,
        string? value,
        string? matchValue,
        string matchFieldName,
        string? friendlyName = null)
    {
        var name = friendlyName ?? FormatFieldName(field);
        _fieldValues[field] = value;

        if (value != matchValue)
        {
            return SetError(field, new ValidationMessage(
                $"{name} must match {matchFieldName}",
                ValidationSeverity.Error,
                "match"));
        }

        return ClearError(field, "match");
    }

    /// <summary>
    /// Adds a custom validation error.
    /// </summary>
    public ValidationResult AddError(string field, string message, string? code = null)
    {
        return SetError(field, new ValidationMessage(
            message,
            ValidationSeverity.Error,
            code ?? "custom"));
    }

    /// <summary>
    /// Adds a validation warning (non-blocking).
    /// </summary>
    public ValidationResult AddWarning(string field, string message, string? code = null)
    {
        return SetError(field, new ValidationMessage(
            message,
            ValidationSeverity.Warning,
            code ?? "warning"));
    }

    /// <summary>
    /// Clears all errors for a field.
    /// </summary>
    public void ClearField(string field)
    {
        if (_fieldErrors.TryGetValue(field, out var errors) && errors.Count > 0)
        {
            errors.Clear();
            OnValidationChanged(field);
        }
    }

    /// <summary>
    /// Clears all validation state.
    /// </summary>
    public void ClearAll()
    {
        var fieldsWithErrors = _fieldErrors.Keys.ToList();
        _fieldErrors.Clear();
        _fieldValues.Clear();

        foreach (var field in fieldsWithErrors)
        {
            OnValidationChanged(field);
        }
    }

    /// <summary>
    /// Gets the validation result for a specific field.
    /// </summary>
    public ValidationResult GetFieldResult(string field)
    {
        if (!_fieldErrors.TryGetValue(field, out var errors) || errors.Count == 0)
        {
            return new ValidationResult(true, [], _fieldValues.GetValueOrDefault(field));
        }

        return new ValidationResult(
            errors.All(e => e.Severity != ValidationSeverity.Error),
            errors,
            _fieldValues.GetValueOrDefault(field));
    }

    /// <summary>
    /// Validates all fields and returns a summary.
    /// </summary>
    public ValidationSummary ValidateAll()
    {
        var errorCount = _fieldErrors.Values.Sum(errors =>
            errors.Count(e => e.Severity == ValidationSeverity.Error));
        var warningCount = _fieldErrors.Values.Sum(errors =>
            errors.Count(e => e.Severity == ValidationSeverity.Warning));

        return new ValidationSummary(
            IsValid: errorCount == 0,
            ErrorCount: errorCount,
            WarningCount: warningCount,
            FirstInvalidField: FirstInvalidField,
            Errors: Errors);
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private ValidationResult SetError(string field, ValidationMessage message)
    {
        if (!_fieldErrors.TryGetValue(field, out var errors))
        {
            errors = [];
            _fieldErrors[field] = errors;
        }

        // Replace existing error with same code
        errors.RemoveAll(e => e.Code == message.Code);
        errors.Add(message);

        OnValidationChanged(field);

        return new ValidationResult(
            message.Severity != ValidationSeverity.Error,
            errors,
            _fieldValues.GetValueOrDefault(field));
    }

    private ValidationResult ClearError(string field, string code)
    {
        if (_fieldErrors.TryGetValue(field, out var errors))
        {
            var removed = errors.RemoveAll(e => e.Code == code);
            if (removed > 0)
            {
                OnValidationChanged(field);
            }
        }

        var remainingErrors = _fieldErrors.GetValueOrDefault(field) ?? [];
        return new ValidationResult(
            remainingErrors.All(e => e.Severity != ValidationSeverity.Error),
            remainingErrors,
            _fieldValues.GetValueOrDefault(field));
    }

    private void OnValidationChanged(string field)
    {
        ValidationChanged?.Invoke(this, new ValidationChangedEventArgs(
            field,
            GetFieldResult(field),
            IsValid));
    }

    private static string FormatFieldName(string field)
    {
        // Convert camelCase/PascalCase to Title Case
        var result = Regex.Replace(field, "([a-z])([A-Z])", "$1 $2");
        result = Regex.Replace(result, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        return char.ToUpper(result[0]) + result[1..];
    }

    [GeneratedRegex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_-]*$")]
    private static partial Regex IdentifierRegex();
}

// ============================================================================
// Validation Types
// ============================================================================

/// <summary>
/// Result of a validation check.
/// </summary>
public sealed record ValidationResult(
    bool IsValid,
    IReadOnlyList<ValidationMessage> Messages,
    object? PreservedValue);

/// <summary>
/// A validation message with severity and code.
/// </summary>
public sealed record ValidationMessage(
    string Message,
    ValidationSeverity Severity,
    string Code);

/// <summary>
/// Validation severity level.
/// </summary>
public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Summary of all validation state.
/// </summary>
public sealed record ValidationSummary(
    bool IsValid,
    int ErrorCount,
    int WarningCount,
    string? FirstInvalidField,
    IReadOnlyDictionary<string, IReadOnlyList<ValidationMessage>> Errors);

/// <summary>
/// Event args for validation state changes.
/// </summary>
public sealed class ValidationChangedEventArgs : EventArgs
{
    public string Field { get; }
    public ValidationResult Result { get; }
    public bool IsFormValid { get; }

    public ValidationChangedEventArgs(string field, ValidationResult result, bool isFormValid)
    {
        Field = field;
        Result = result;
        IsFormValid = isFormValid;
    }
}

/// <summary>
/// Extension methods for validation.
/// </summary>
public static class ValidationExtensions
{
    /// <summary>
    /// Chains multiple validations for a field.
    /// </summary>
    public static ValidationResult Then(
        this ValidationResult result,
        Func<ValidationResult> nextValidation)
    {
        // Only run next validation if current passed
        if (!result.IsValid)
        {
            return result;
        }

        return nextValidation();
    }
}
