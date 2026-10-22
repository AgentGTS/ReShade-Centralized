using System.Net;

namespace ReShadeCentralized;

public static class UserErrors
{
    public static string Describe(Exception error)
    {
        string? instruction = error switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                "The download is no longer available. For shader packages, open Shader packages > " +
                "Refresh catalog and retry. For ReShade, use Runtime & storage > Import downloaded installers.",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
                "The server refused the download or limited requests. Wait before retrying and check " +
                "whether your network permits access to the download site.",
            HttpRequestException =>
                "Check your internet connection and access to the download site, then retry the action.",
            OperationCanceledException =>
                "The download timed out. Check your internet connection and retry the action.",
            UnauthorizedAccessException =>
                "Run ReShade Centralized as administrator and check write permissions for the application " +
                "and central folders shown in Runtime & storage.",
            IOException when (error.HResult & 0xFFFF) is 32 or 33 =>
                "A file is in use. Close affected games and any editor using these files, then retry.",
            IOException when (error.HResult & 0xFFFF) is 39 or 112 =>
                "Free disk space on the drives containing the application and central folders, then retry.",
            _ => null
        };

        return instruction == null ? error.Message : $"{instruction}\n\nDetails: {error.Message}";
    }
}
