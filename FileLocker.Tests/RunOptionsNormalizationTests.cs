using System.Reflection;
using System.Runtime.Serialization;

namespace FileLocker.Tests;

#pragma warning disable SYSLIB0050
public sealed class RunOptionsNormalizationTests
{
    [Fact]
    public void NormalizeRunOptionsForCurrentMode_PreservesUnlockMaterialForReadOperationsInBeginnerMode()
    {
        byte[] keyfileBytes = [1, 2, 3, 4];
        object window = CreateWindow(UserExperienceLevel.Beginner);
        object options = CreateOptions(keyfileBytes, recoveryKey: "recovery-key");

        object normalized = InvokeNormalize(window, options, encryptingNewPayload: false);

        Assert.Equal("key.bin", GetProperty<string?>(normalized, "KeyfilePath"));
        Assert.Same(keyfileBytes, GetProperty<byte[]?>(normalized, "KeyfileBytes"));
        Assert.Equal("recovery-key", GetProperty<string?>(normalized, "RecoveryKey"));
        Assert.Equal(keyfileBytes, [1, 2, 3, 4]);
    }

    [Fact]
    public void NormalizeRunOptionsForCurrentMode_StillHidesAdvancedMaterialForBeginnerEncryption()
    {
        byte[] keyfileBytes = [1, 2, 3, 4];
        object window = CreateWindow(UserExperienceLevel.Beginner);
        object options = CreateOptions(keyfileBytes, recoveryKey: "recovery-key");

        object normalized = InvokeNormalize(window, options, encryptingNewPayload: true);

        Assert.Null(GetProperty<string?>(normalized, "KeyfilePath"));
        Assert.Null(GetProperty<byte[]?>(normalized, "KeyfileBytes"));
        Assert.Null(GetProperty<string?>(normalized, "RecoveryKey"));
        Assert.Equal([0, 0, 0, 0], keyfileBytes);
    }

    [Fact]
    public void NormalizeRunOptionsForCurrentMode_UsesCatalogAlgorithmAndKeySizeForEncryption()
    {
        object window = CreateWindow(UserExperienceLevel.Advanced);
        object options = CreateOptions(
            keyfileBytes: [],
            recoveryKey: "",
            algorithm: "chacha20 poly1305 ietf",
            keySizeBits: 128);

        object normalized = InvokeNormalize(window, options, encryptingNewPayload: true);

        Assert.Equal(EncryptionAlgorithmCatalog.ChaCha20Poly1305, GetProperty<string>(normalized, "Algorithm"));
        Assert.Equal(256, GetProperty<int>(normalized, "KeySizeBits"));
    }

    [Fact]
    public void NormalizeRunOptionsForCurrentMode_ClearsSecureDeleteWhenOriginalsAreRetained()
    {
        object window = CreateWindow(UserExperienceLevel.Advanced);
        object options = CreateOptions(
            keyfileBytes: [],
            recoveryKey: "",
            removeOriginalsAfterSuccess: false,
            secureDeleteOriginals: true);

        object normalized = InvokeNormalize(window, options, encryptingNewPayload: true);

        Assert.False(GetProperty<bool>(normalized, "RemoveOriginalsAfterSuccess"));
        Assert.False(GetProperty<bool>(normalized, "SecureDeleteOriginals"));
    }

    [Fact]
    public void NormalizeRunOptionsForCurrentMode_CapsMetadataOverrideText()
    {
        object window = CreateWindow(UserExperienceLevel.Advanced);
        object options = CreateOptions(
            keyfileBytes: [],
            recoveryKey: "",
            metadataLabel: $"  {new string('L', 300)}  ",
            metadataNotes: $"first\r\nsecond\0third {new string('N', 5000)}",
            metadataCreatedText: $" 2026-05-29\0x {new string('C', 200)} ");

        object normalized = InvokeNormalize(window, options, encryptingNewPayload: true);
        object metadata = GetProperty<object>(normalized, "Metadata");

        Assert.Equal(new string('L', 256), GetProperty<string>(metadata, "Label"));
        string notes = GetProperty<string>(metadata, "Notes");
        Assert.True(notes.Length <= 4096);
        Assert.Contains("first\nsecond third", notes);
        Assert.DoesNotContain('\0', notes);
        string createdText = GetProperty<string>(metadata, "CreatedText");
        Assert.True(createdText.Length <= 128);
        Assert.DoesNotContain('\0', createdText);
    }

    [Fact]
    public void NormalizeRunOptionsForCurrentMode_CleansMetadataUnicodeFormatCharacters()
    {
        object window = CreateWindow(UserExperienceLevel.Advanced);
        object options = CreateOptions(
            keyfileBytes: [],
            recoveryKey: "",
            metadataLabel: "Label\u202EText",
            metadataNotes: "first\u202Esecond",
            metadataCreatedText: "2026-05-29\u202E12:00");

        object normalized = InvokeNormalize(window, options, encryptingNewPayload: true);
        object metadata = GetProperty<object>(normalized, "Metadata");

        Assert.Equal("Label Text", GetProperty<string>(metadata, "Label"));
        Assert.Equal("first second", GetProperty<string>(metadata, "Notes"));
        Assert.Equal("2026-05-29 12:00", GetProperty<string>(metadata, "CreatedText"));
    }

    [Fact]
    public void NormalizeProfileName_CleansUnicodeFormatCharacters()
    {
        string normalized = InvokeNormalizeProfileName("Custom\u202EProfile");

        Assert.Equal("Custom Profile", normalized);
    }

    private static object CreateWindow(UserExperienceLevel experienceLevel)
    {
        object window = FormatterServices.GetUninitializedObject(typeof(MainWindow));
        FieldInfo field = typeof(MainWindow).GetField("_currentExperienceLevel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_currentExperienceLevel field not found.");
        field.SetValue(window, experienceLevel);
        return window;
    }

    private static object InvokeNormalize(object window, object options, bool encryptingNewPayload)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("NormalizeRunOptionsForCurrentMode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NormalizeRunOptionsForCurrentMode method not found.");
        return method.Invoke(window, [options, encryptingNewPayload])
            ?? throw new InvalidOperationException("NormalizeRunOptionsForCurrentMode returned null.");
    }

    private static string InvokeNormalizeProfileName(string profileName)
    {
        MethodInfo method = typeof(MainWindow).GetMethod("NormalizeProfileName", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NormalizeProfileName method not found.");
        return (string?)method.Invoke(null, [profileName])
            ?? throw new InvalidOperationException("NormalizeProfileName returned null.");
    }

    private static object CreateOptions(
        byte[] keyfileBytes,
        string recoveryKey,
        string algorithm = EncryptionAlgorithmCatalog.Aes256Gcm,
        int keySizeBits = 256,
        bool removeOriginalsAfterSuccess = false,
        bool secureDeleteOriginals = false,
        string metadataLabel = "",
        string metadataNotes = "",
        string metadataCreatedText = "",
        string metadataModifiedText = "")
    {
        Type metadataType = typeof(MainWindow).GetNestedType("MetadataOverridesSnapshot", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MetadataOverridesSnapshot type not found.");
        Type optionsType = typeof(MainWindow).GetNestedType("ProcessingRunOptions", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProcessingRunOptions type not found.");

        object metadata = Activator.CreateInstance(
            metadataType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [metadataLabel, metadataNotes, false, metadataCreatedText, metadataModifiedText],
            culture: null) ?? throw new InvalidOperationException("Could not create metadata snapshot.");

        return Activator.CreateInstance(
            optionsType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                true,
                false,
                false,
                algorithm,
                "Encrypt / Decrypt",
                keySizeBits,
                removeOriginalsAfterSuccess,
                secureDeleteOriginals,
                true,
                false,
                "",
                false,
                "",
                true,
                false,
                false,
                "Current time",
                "",
                "key.bin",
                keyfileBytes,
                recoveryKey,
                "Test",
                metadata
            ],
            culture: null) ?? throw new InvalidOperationException("Could not create processing options.");
    }

    private static T GetProperty<T>(object target, string propertyName)
    {
        PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property not found: {propertyName}");
        return (T)property.GetValue(target)!;
    }
}
#pragma warning restore SYSLIB0050
