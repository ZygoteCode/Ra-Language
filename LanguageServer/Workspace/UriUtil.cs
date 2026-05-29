using System;

namespace RaLanguage.LanguageServer.Workspace
{
    /// <summary>
    /// Conversions between LSP document URIs and local filesystem paths. Uses
    /// <see cref="System.Uri"/> (AOT-safe, no reflection) for correct cross-platform
    /// percent-decoding and drive/UNC handling.
    /// </summary>
    public static class UriUtil
    {
        /// <summary>
        /// Resolve a <c>file:</c> URI to a local OS path. Non-file URIs (e.g.
        /// <c>untitled:</c>) are returned unchanged so they can still serve as a
        /// lexer "file name" and a stable store key.
        /// </summary>
        public static string ToFileSystemPath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return uri;
            try
            {
                var u = new Uri(uri);
                if (u.IsFile) return u.LocalPath;
                return uri;
            }
            catch
            {
                return uri;
            }
        }

        /// <summary>Build a <c>file:</c> URI from a local path (for result locations).</summary>
        public static string FromFileSystemPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                return new Uri(path).AbsoluteUri;
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// Normalize a URI for use as a document-store key. Editors are not always
        /// byte-consistent about drive-letter casing / percent-encoding, so we round
        /// trip through <see cref="System.Uri"/> when possible.
        /// </summary>
        public static string NormalizeKey(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return uri;
            try
            {
                return new Uri(uri).AbsoluteUri;
            }
            catch
            {
                return uri;
            }
        }
    }
}
