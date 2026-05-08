namespace FileLocker;

internal static class UiStateRules
{
    internal static bool CanStartEncryption(
        bool isProcessing,
        int selectedFileCount,
        bool hasPassword,
        bool passwordsMatch,
        bool outputSettingsValid)
    {
        return !isProcessing &&
            selectedFileCount > 0 &&
            hasPassword &&
            passwordsMatch &&
            outputSettingsValid;
    }

    internal static bool CanStartDecryption(
        bool isProcessing,
        int decryptableCount,
        bool hasUnsupportedFiles,
        bool hasPassword,
        bool outputSettingsValid)
    {
        return !isProcessing &&
            decryptableCount > 0 &&
            !hasUnsupportedFiles &&
            hasPassword &&
            outputSettingsValid;
    }
}
