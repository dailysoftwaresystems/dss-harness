using System.Text;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>The one rule of binary content: a NUL among the first bytes or characters of git's own window, and none past it.</summary>
public sealed class BinaryContentTests
{
    /// <summary>A NUL on the window's last byte or character makes content binary; one just past it does not, and none at all does not.</summary>
    [Fact]
    public void ANulInsideGitsWindow_IsBinary_AndOnePastItIsNot()
    {
        Assert.Equal(8000, BinaryContent.Window);

        var bytes = Enumerable.Repeat((byte)'a', BinaryContent.Window + 10).ToArray();
        Assert.False(BinaryContent.Holds(bytes));
        Assert.False(BinaryContent.Holds(ReadOnlySpan<byte>.Empty));

        bytes[BinaryContent.Window] = 0;
        Assert.False(BinaryContent.Holds(bytes));

        bytes[BinaryContent.Window - 1] = 0;
        Assert.True(BinaryContent.Holds(bytes));
        Assert.True(BinaryContent.Holds(new byte[] { 0 }));

        var text = new string('a', BinaryContent.Window) + "\0";
        Assert.False(BinaryContent.Holds(text));
        Assert.True(BinaryContent.Holds(text.AsSpan(1)));
        Assert.False(BinaryContent.Holds(string.Empty));
    }

    /// <summary>
    /// The conversion rule calls binary exactly what git's own <c>text=auto</c> does, file by file: at its edges - a NUL
    /// past the window, a lone carriage return, one control byte for every 128 that print and one more, a final
    /// end-of-file mark - and over many files of mixed bytes.
    /// </summary>
    [Fact]
    public async Task TheConversionRule_IsGitsOwn_FileByFile()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        File.WriteAllText(temp.Combine(".gitattributes"), "*.t text=auto eol=lf\n");

        var a = (byte)'a';
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["empty.t"] = [],
            ["crlf.t"] = [.. "one\r\ntwo\r\n"u8],
            ["late-nul.t"] = [.. Enumerable.Repeat(a, 9000), 0],
            ["lone-cr.t"] = [.. "one\rtwo\n"u8],
            ["final-cr.t"] = [.. "one\r"u8],
            ["ratio-even.t"] = [.. Enumerable.Repeat(a, 128), 1],
            ["ratio-over.t"] = [.. Enumerable.Repeat(a, 128), 1, 1],
            ["ratio-short.t"] = [.. Enumerable.Repeat(a, 127), 1],
            ["printing-controls.t"] = [.. Enumerable.Repeat((byte)'\b', 5), (byte)'\t', 0x1B, (byte)'\f', a],
            ["delete.t"] = [.. Enumerable.Repeat(a, 10), 0x7F],
            ["eof-mark.t"] = [.. Enumerable.Repeat(a, 10), 0x1A],
            ["eof-marks.t"] = [.. Enumerable.Repeat(a, 10), 0x1A, 0x1A],
            ["high.t"] = [.. Encoding.UTF8.GetBytes("\u00e9t\u00e9 \U0001F600\n")],
        };

        // Many files of mixed bytes: each with its own share of control bytes, around one in 128 that print, and a few with
        // a NUL or a carriage return of their own.
        byte[] printing = [.. Enumerable.Repeat(a, 60), .. Enumerable.Repeat((byte)' ', 5), .. Enumerable.Repeat((byte)'\n', 10), (byte)'\t', 0x1B, (byte)'\b', (byte)'\f', 0x80, 0xC3, 0xFF];
        byte[] controls = [1, 2, 0x1F, 0x7F, 0x1A];
        var random = new Random(20_23);

        for (var at = 0; at < 200; at++)
        {
            var bytes = new List<byte>();
            var share = random.NextDouble() / 64;
            var stray = at % 10 == 0;

            for (var length = random.Next(0, 3000); bytes.Count < length;)
            {
                var roll = random.NextDouble();

                if (stray && roll < 0.001)
                {
                    bytes.Add(random.Next(2) == 0 ? (byte)0 : (byte)'\r');
                }
                else if (roll < 0.05)
                {
                    bytes.AddRange("\r\n"u8);
                }
                else if (roll < 0.05 + share)
                {
                    bytes.Add(controls[random.Next(controls.Length)]);
                }
                else
                {
                    bytes.Add(printing[random.Next(printing.Length)]);
                }
            }

            files[$"mixed{at}.t"] = [.. bytes];
        }

        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(temp.Combine(name), bytes);
        }

        await harness.RunGitAsync(temp.Path, ["add", "-A"], cancellationToken);
        var listed = (await harness.RunGitAsync(temp.Path, ["ls-files", "--eol", "-z"], cancellationToken)).StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => (Path: record[(record.IndexOf('\t', StringComparison.Ordinal) + 1)..], Fields: record[..record.IndexOf('\t', StringComparison.Ordinal)].Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .ToDictionary(entry => entry.Path, entry => entry.Fields.Single(field => field.StartsWith("w/", StringComparison.Ordinal)), StringComparer.Ordinal);

        Assert.True(files.Values.Count(bytes => BinaryContent.ForConversion(bytes)) > 20, "too few of the mixed files are binary to show the rule");
        Assert.True(files.Values.Count(bytes => !BinaryContent.ForConversion(bytes)) > 20, "too few of the mixed files are text to show the rule");
        Assert.All(files, file => Assert.True(BinaryContent.ForConversion(file.Value) == (listed[file.Key] == "w/-text"), $"{file.Key}: git says {listed[file.Key]}"));
    }
}
