namespace octo_fiesta.Services.Common;

#region Proposed Error Class
/// <summary>
/// Represents a simple error with a string Error Message and optional reason.
/// </summary>
/// <param name="message">Simple string error message</param>
/// <param name="reason">Another instance or Error which caused this Error</param>
public class Error(string message, Error? reason = null)
{
    /// <summary>
    /// Error Message
    /// </summary>
    public string Message { get; } = message;

    /// <summary>
    /// Error Reason.
    /// Another instance or Error which caused this Error
    /// </summary>
    public Error? Reason { get; } = reason;

    /// <summary>
    /// Get the full chain of errors, including current one.
    /// Current error first, original root error last.
    /// </summary>
    public List<Error> GetTrace()
    {
        Error? error = this;
        List<Error> errors = [];
        
        while (error is not null)
        {
            errors.Add(error);
            error = error.Reason;
        }

        return errors;
    }
    /// <summary>
    /// Overrides ToString()
    /// </summary>
    /// <returns>Error message prefixed by Error type name.</returns>
    public override string ToString()
    {
        var type = GetType();
        string? fullName = type.FullName;
        string name = fullName?
                    .Split(".")?[^1]?
                    .Replace("+",".")
                    ?? type.Name;

        return $"{name}: {Message}";
    }
}
#endregion Error Definition

#region Demo
/// <summary>
/// Some random inheritance
/// </summary>
public class SquidWTFError(string message, Error? reason = null) : Error(message, reason);

/// <summary>
/// And another random inheritance with overridden constructor.
///
/// Yes, constructors are public, there are no static factory methods.
/// </summary>
public class SquidWTFSongNotDownloaded(int songId, Error? reason = null)
    : SquidWTFError($"Failed to download song with id {songId}", reason)
{
    public int SongId { get; } = songId;
}



/// <summary>
/// And better approach to organize these types of errors.
/// Nested tree-like structure of subclasses. It may be convenient
/// to put them into separate namespaces or something.
///
/// Typing and nesting hierarchy can serve as replacement for `ErrorType Type`
/// and possibly for `string Code`.
/// Anyways we can extend base Error with some fields and methods.
/// </summary>
public class HttpError(string message, Error? reason = null) : Error(message, reason)
{
    public class NotFound(string message) : HttpError(message, null);
    public class Internal(string message) : HttpError(message, null);
    public class Unauthorized(string message) : HttpError(message, null);
}


/// <summary>
/// Silly demo of possible typed error usage
/// </summary>
public static class Demo
{
    /// <summary>
    /// Returning different implicitly converted Results
    /// with different Error subtypes
    /// </summary>
    public static Result<string> GetSongResponse(string url)
    {
        bool responseIsSuccess = false;
        int responseCode = 404;
        string responseString = "";

        if (responseIsSuccess) return responseString;

        if (responseCode == 401) return new HttpError.Unauthorized(url);

        if (responseCode == 404) return new HttpError.NotFound(url);

        return new HttpError("Impossible HTTP Error happened. Panic please. Please. Wai.. shoudn't I use exceptions here?");
    }

    /// <summary>
    /// Returning Error with reason. May be useful for logging and tracing.
    /// </summary>
    public static Result DownloadSong(int id)
    {
        var res = GetSongResponse($"https://monochrome.tf/song/{id}");
        if (res.IsFailure){
            return new SquidWTFSongNotDownloaded(id, res.Error);
        }
        // do some downloading job
        return Result.Success();
    }

    /// <summary>
    /// Checking Error Type.
    /// Printing Error trace.
    /// </summary>
    /// <param name="ids"></param>
    public static void DownloadSongs(List<int> ids)
    {
        foreach (int id in ids)
        {
            var result = DownloadSong(id);
            if (result.IsSuccess)
            {
                ///
            }

            if (result.Error is SquidWTFSongNotDownloaded error)
            {
                error.GetTrace().ForEach(e => Console.WriteLine(e));
            }
        }
    }
}


#endregion