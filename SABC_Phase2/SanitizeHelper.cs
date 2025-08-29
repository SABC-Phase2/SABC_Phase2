namespace SABC_Phase2
{
    /// <summary>
    /// Provides helper methods for sanitizing input values for use in enterprise integrations,
    /// specifically for SharePoint folder and file naming.
    /// </summary>
    public static class SanitizeHelper
    {
        /// <summary>
        /// Converts a given string into a SharePoint-safe folder or file name by replacing
        /// all forbidden characters with an underscore ('_').
        /// Periods ('.') are now allowed to preserve file extensions.
        /// Only the base name (not the extension) should be sanitized for files!
        /// </summary>
        /// <param name="input">
        /// The input string which may include forbidden characters.
        /// </param>
        /// <returns>
        /// A sanitized string, safe for use as a folder or file name in SharePoint.
        /// </returns>
        public static string ToSharePointSafeFolderName(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            // List of characters forbidden by SharePoint in folder/file names, 
            // EXCLUDING the period ('.') to preserve file extensions.
            var forbidden = new[] { "/", "\\", ":", "*", "?", "\"", "<", ">", "|", "#", "%", "~" };
            string safe = input;
            foreach (var ch in forbidden)
                safe = safe.Replace(ch, "_");
            // Optionally trim spaces and underscores from start/end
            return safe.Trim(' ', '_');
        }

        /// <summary>
        /// Sanitizes a filename by preserving the extension, only sanitizing the base name.
        /// </summary>
        /// <param name="fileName">The original filename (may include extension).</param>
        /// <returns>A sanitized filename safe for SharePoint, with its extension intact.</returns>
        public static string ToSharePointSafeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return string.Empty;
            var extension = System.IO.Path.GetExtension(fileName);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var safeBase = ToSharePointSafeFolderName(baseName);
            return string.IsNullOrEmpty(extension) ? safeBase : $"{safeBase}{extension}";
        }
    }
}