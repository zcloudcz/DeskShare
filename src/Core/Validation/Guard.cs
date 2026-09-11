namespace DeskShare.Core.Validation;

/// <summary>
/// Centralized validation helpers to reduce code duplication and improve consistency.
/// Provides common argument validation methods with standardized error messages.
/// </summary>
/// <remarks>
/// Example usage:
/// <code>
/// public void RegisterClient(string clientId, WebSocket socket)
/// {
///     Guard.NotNullOrWhiteSpace(clientId, nameof(clientId));
///     Guard.NotNull(socket, nameof(socket));
///     // ...
/// }
/// </code>
/// </remarks>
public static class Guard
{
    /// <summary>
    /// Validates that a string is not null, empty, or whitespace.
    /// </summary>
    /// <param name="value">The string value to validate</param>
    /// <param name="paramName">The name of the parameter (for error messages)</param>
    /// <exception cref="ArgumentException">Thrown when value is null, empty, or whitespace</exception>
    public static void NotNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{paramName} cannot be null, empty, or whitespace.", paramName);
        }
    }

    /// <summary>
    /// Validates that an object reference is not null.
    /// </summary>
    /// <typeparam name="T">The type of object being validated</typeparam>
    /// <param name="value">The object reference to validate</param>
    /// <param name="paramName">The name of the parameter (for error messages)</param>
    /// <exception cref="ArgumentNullException">Thrown when value is null</exception>
    public static void NotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
    }

    /// <summary>
    /// Validates that a numeric value is within a specified range (inclusive).
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="min">Minimum allowed value (inclusive)</param>
    /// <param name="max">Maximum allowed value (inclusive)</param>
    /// <param name="paramName">The name of the parameter (for error messages)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when value is outside the valid range</exception>
    public static void InRange(int value, int min, int max, string paramName)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(paramName,
                $"{paramName} must be between {min} and {max} (inclusive). Actual value: {value}");
        }
    }

    /// <summary>
    /// Validates that a numeric value is within a specified range (inclusive).
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="min">Minimum allowed value (inclusive)</param>
    /// <param name="max">Maximum allowed value (inclusive)</param>
    /// <param name="paramName">The name of the parameter (for error messages)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when value is outside the valid range</exception>
    public static void InRange(double value, double min, double max, string paramName)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(paramName,
                $"{paramName} must be between {min} and {max} (inclusive). Actual value: {value}");
        }
    }

    /// <summary>
    /// Validates that a collection is not null or empty.
    /// </summary>
    /// <typeparam name="T">The type of elements in the collection</typeparam>
    /// <param name="value">The collection to validate</param>
    /// <param name="paramName">The name of the parameter (for error messages)</param>
    /// <exception cref="ArgumentNullException">Thrown when collection is null</exception>
    /// <exception cref="ArgumentException">Thrown when collection is empty</exception>
    public static void NotNullOrEmpty<T>(IEnumerable<T>? value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        if (!value.Any())
        {
            throw new ArgumentException($"{paramName} cannot be empty.", paramName);
        }
    }

    /// <summary>
    /// Validates that a value is positive (greater than zero).
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="paramName">The name of the parameter (for error messages)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when value is not positive</exception>
    public static void Positive(int value, string paramName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName,
                $"{paramName} must be positive (greater than 0). Actual value: {value}");
        }
    }

    /// <summary>
    /// Validates that a value is non-negative (zero or greater).
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="paramName">The name of the parameter (for error messages)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when value is negative</exception>
    public static void NonNegative(int value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName,
                $"{paramName} must be non-negative (>= 0). Actual value: {value}");
        }
    }

    /// <summary>
    /// Validates that a boolean condition is true.
    /// </summary>
    /// <param name="condition">The condition to check</param>
    /// <param name="message">The error message if condition is false</param>
    /// <exception cref="ArgumentException">Thrown when condition is false</exception>
    public static void Requires(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }

    /// <summary>
    /// Validates that an enum value is defined in its enum type.
    /// </summary>
    /// <typeparam name="TEnum">The enum type</typeparam>
    /// <param name="value">The enum value to validate</param>
    /// <param name="paramName">The name of the parameter (for error messages)</param>
    /// <exception cref="ArgumentException">Thrown when value is not a valid enum member</exception>
    public static void EnumDefined<TEnum>(TEnum value, string paramName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(typeof(TEnum), value))
        {
            throw new ArgumentException(
                $"{paramName} has an invalid value '{value}'. Valid values are: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}",
                paramName);
        }
    }
}
