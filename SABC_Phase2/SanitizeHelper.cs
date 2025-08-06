namespace SABC_Phase2
{
    /// <summary>
    /// Provides helper methods for sanitizing input values for use in enterprise integrations,
    /// specifically for SharePoint folder and file naming.
    /// </summary>
    public static class SanitizeHelper
    {
        /// <summary>
        /// Converts a given tender number string into a SharePoint-safe folder name by replacing
        /// all forbidden characters with an underscore ('_').
        /// 
        /// SharePoint does not allow the following characters in folder or file names:
        ///   / \ : * ? " < > | #
        /// This method ensures that folder names derived from user or business input (like tender numbers)
        /// do not cause SharePoint API failures or security issues.
        /// </summary>
        /// <param name="tenderNumber">
        /// The tender number or identifier string which may include forbidden characters.
        /// </param>
        /// <returns>
        /// A sanitized string, safe for use as a folder name in SharePoint.
        /// </returns>
        public static string ToSharePointSafeFolderName(string tenderNumber)
        {
            // List of characters forbidden by SharePoint in folder/file names.
            var forbidden = new[] { "/", "\\", ":", "*", "?", "\"", "<", ">", "|", "#" };

            // Start with the original value and replace each forbidden character with '_'
            string safe = tenderNumber;
            foreach (var ch in forbidden)
                safe = safe.Replace(ch, "_");

            // Return the sanitized folder name
            return safe;
        }
    }
}