namespace JoePro.Core;

/// <summary>
/// A FoxPro runtime error. <see cref="Number"/> uses VFP's error numbers so that ERROR(),
/// AERROR() and ON ERROR handlers in migrated code keep working.
/// </summary>
public class VfpException : Exception
{
    public int Number { get; }
    /// <summary>Optional extra detail (for example, the missing variable name).</summary>
    public string? Detail { get; }

    public VfpException(int number, string message, string? detail = null) : base(message)
    {
        Number = number;
        Detail = detail;
    }

    public static VfpException FileNotFound(string file) => new(ErrorCodes.FileDoesNotExist, $"File '{file}' does not exist.", file);
    public static VfpException EndOfFile() => new(ErrorCodes.EndOfFile, "End of file encountered.");
    public static VfpException BeginningOfFile() => new(ErrorCodes.BeginningOfFile, "Beginning of file encountered.");
    public static VfpException TypeMismatch() => new(ErrorCodes.DataTypeMismatch, "Data type mismatch.");
    public static VfpException OperatorTypeMismatch() => new(ErrorCodes.OperatorOperandMismatch, "Operator/operand type mismatch.");
    public static VfpException Syntax(string? detail = null) => new(ErrorCodes.SyntaxError, "Syntax error.", detail);
    public static VfpException InvalidArgument() => new(ErrorCodes.InvalidArgument, "Function argument value, type, or count is invalid.");
    public static VfpException VariableNotFound(string name) => new(ErrorCodes.VariableNotFound, $"Variable '{name.ToUpperInvariant()}' is not found.", name);
    public static VfpException AliasNotFound(string alias) => new(ErrorCodes.AliasNotFound, $"Alias '{alias.ToUpperInvariant()}' is not found.", alias);
    public static VfpException NoTableOpen() => new(ErrorCodes.NoTableOpen, "No table is open in the current work area.");
    public static VfpException FieldNotFound(string name) => new(ErrorCodes.VariableNotFound, $"Variable '{name.ToUpperInvariant()}' is not found.", name);
    public static VfpException UniqueViolation(string tag) => new(ErrorCodes.UniquenessViolated, $"Uniqueness of index {tag.ToUpperInvariant()} is violated.", tag);
    public static VfpException Unrecognized(string verb) => new(ErrorCodes.UnrecognizedVerb, "Unrecognized command verb.", verb);
    public static VfpException InvalidSubscript() => new(ErrorCodes.InvalidSubscript, "Invalid subscript reference.");
    public static VfpException NotSupported(string feature) => new(ErrorCodes.FeatureNotAvailable, $"Feature is not available: {feature}.", feature);
}

/// <summary>VFP error numbers used by the runtime.</summary>
public static class ErrorCodes
{
    public const int FileDoesNotExist = 1;
    public const int FileInUse = 3;
    public const int EndOfFile = 4;
    public const int DataTypeMismatch = 9;
    public const int SyntaxError = 10;
    public const int InvalidArgument = 11;
    public const int VariableNotFound = 12;
    public const int AliasNotFound = 13;
    public const int UnrecognizedVerb = 16;
    public const int InvalidSubscript = 31;
    public const int UnrecognizedPhrase = 36;
    public const int BeginningOfFile = 38;
    public const int NoTableOpen = 52;
    public const int OperatorOperandMismatch = 107;
    public const int UserDefined = 1098;
    public const int FeatureNotAvailable = 1999;
    public const int UniquenessViolated = 1884;
    public const int FileAccessDenied = 1705;
    public const int ProcedureNotFound = 1;
}
