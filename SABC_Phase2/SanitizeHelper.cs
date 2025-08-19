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
            // List of characters forbidden by SharePoint in folder/file names.
            var forbidden = new[] { "/", "\\", ":", "*", "?", "\"", "<", ">", "|", "#", "%", "~", "." };
            string safe = input;
            foreach (var ch in forbidden)
                safe = safe.Replace(ch, "_");
            // Optionally trim spaces and underscores from start/end
            return safe.Trim(' ', '_');
        }
    }
}