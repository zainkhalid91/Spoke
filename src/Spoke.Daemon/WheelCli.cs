using Spoke.Core;

namespace Spoke.Daemon;

/// <summary>
/// <c>Spoke.exe --export "Dev" dev.spoke</c> (a wheel by name or number) and <c>Spoke.exe --import dev.spoke</c>.
/// Import adds the wheel to settings.json; a running Spoke picks it up on its own.
/// </summary>
internal static class WheelCli
{
    public static int Export(string which, string output)
    {
        if (Load(out var config, out string text) is { } err) return Fail(err);
        var wheels = config.Wheels!;
        int index = int.TryParse(which, out int n) ? n - 1 : wheels.FindIndex(w => string.Equals((w.Name ?? "").Trim(), which.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= wheels.Count)
            return Fail($"There's no wheel \"{which}\". Wheels: {string.Join(", ", wheels.Select((w, i) => $"{i + 1} {w.Name}"))}.");
        File.WriteAllText(output, WheelFile.Export(wheels[index]));
        Console.WriteLine($"Saved \"{wheels[index].Name}\" to {output}");
        return 0;
    }

    public static int Import(string input)
    {
        if (!File.Exists(input)) return Fail($"{input} doesn't exist.");
        if (Load(out var config, out string text) is { } err) return Fail(err);
        var wheel = WheelFile.Import(File.ReadAllText(input), config.Wheels!.Select(w => w.Name ?? ""), out string? error);
        if (wheel is null) return Fail(error!);
        if (config.Wheels!.Count >= ConfigLoader.MaxWheels) return Fail($"Spoke holds up to {ConfigLoader.MaxWheels} wheels; remove one first.");
        config.Wheels.Add(wheel);
        string json = ConfigLoader.WithConfig(text, config);
        var check = ConfigLoader.Parse(json);
        if (!check.Ok) return Fail(check.Errors[0]);
        File.WriteAllText(Settings.FilePath, json);
        Console.WriteLine($"Added the wheel \"{wheel.Name}\" ({wheel.Slices!.Count} slices). Open Spoke and press Tab to reach it.");
        foreach (var m in WheelFile.Missing(wheel)) Console.WriteLine($"  Not on this PC: {m}");
        return 0;
    }

    /// <summary>The user's settings with every wheel spelled out (the built-in one when the file has none).</summary>
    private static string? Load(out SpokeConfig config, out string text)
    {
        text = File.Exists(Settings.FilePath) ? File.ReadAllText(Settings.FilePath) : "";
        var r = ConfigLoader.Parse(text);
        config = r.Config ?? new SpokeConfig();
        if (!r.Ok) return "settings.json has a problem, so Spoke won't change it: " + r.Errors[0];
        if (r.UsedDefaultWheel) config.Wheels = [ConfigLoader.DefaultWheel(), .. (config.Wheels ?? []).Skip(1)];
        return null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
