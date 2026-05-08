namespace FileLocker.Tests;

public sealed class UiStateRulesTests
{
    [Fact]
    public void CanStartEncryption_RequiresFilePasswordMatchAndOutput()
    {
        Assert.False(UiStateRules.CanStartEncryption(isProcessing: false, selectedFileCount: 0, hasPassword: true, passwordsMatch: true, outputSettingsValid: true));
        Assert.False(UiStateRules.CanStartEncryption(isProcessing: false, selectedFileCount: 1, hasPassword: false, passwordsMatch: true, outputSettingsValid: true));
        Assert.False(UiStateRules.CanStartEncryption(isProcessing: false, selectedFileCount: 1, hasPassword: true, passwordsMatch: false, outputSettingsValid: true));
        Assert.False(UiStateRules.CanStartEncryption(isProcessing: false, selectedFileCount: 1, hasPassword: true, passwordsMatch: true, outputSettingsValid: false));
        Assert.True(UiStateRules.CanStartEncryption(isProcessing: false, selectedFileCount: 1, hasPassword: true, passwordsMatch: true, outputSettingsValid: true));
    }

    [Fact]
    public void CanStartDecryption_RequiresSupportedFilePasswordAndOutput()
    {
        Assert.False(UiStateRules.CanStartDecryption(isProcessing: false, decryptableCount: 0, hasUnsupportedFiles: false, hasPassword: true, outputSettingsValid: true));
        Assert.False(UiStateRules.CanStartDecryption(isProcessing: false, decryptableCount: 1, hasUnsupportedFiles: false, hasPassword: false, outputSettingsValid: true));
        Assert.False(UiStateRules.CanStartDecryption(isProcessing: false, decryptableCount: 1, hasUnsupportedFiles: false, hasPassword: true, outputSettingsValid: false));
        Assert.False(UiStateRules.CanStartDecryption(isProcessing: false, decryptableCount: 1, hasUnsupportedFiles: true, hasPassword: true, outputSettingsValid: true));
        Assert.True(UiStateRules.CanStartDecryption(isProcessing: false, decryptableCount: 1, hasUnsupportedFiles: false, hasPassword: true, outputSettingsValid: true));
    }
}
