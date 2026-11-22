namespace TantivyDotNet;

/// <summary>Categorizes errors reported by the native Tantivy engine.</summary>
public enum TantivyErrorCode
{
    /// <summary>An error with no more specific category.</summary>
    Error = 1,
    /// <summary>A malformed argument: bad schema, invalid document JSON, unknown field, heap too small, etc.</summary>
    InvalidArgument = 2,
    /// <summary>The query string could not be parsed.</summary>
    QueryParse = 3,
    /// <summary>The target directory already contains an index.</summary>
    IndexAlreadyExists = 4,
    /// <summary>A filesystem error, such as a missing directory or a permissions problem.</summary>
    Io = 5,
    /// <summary>Another writer holds the index lock. Only one <see cref="IndexWriter"/> may exist per index.</summary>
    LockBusy = 6,
    /// <summary>The native code hit an internal bug (a Rust panic). Please report it.</summary>
    Panic = 7,
}

/// <summary>An error reported by the native Tantivy engine.</summary>
public class TantivyException : Exception
{
    /// <summary>Creates an exception with the given category and message.</summary>
    public TantivyException(TantivyErrorCode code, string message) : base(message) => Code = code;

    /// <summary>The error category.</summary>
    public TantivyErrorCode Code { get; }
}

/// <summary>Thrown when a query string isn't valid Tantivy query syntax or references unknown fields.</summary>
public sealed class QueryParseException(string message) : TantivyException(TantivyErrorCode.QueryParse, message);
