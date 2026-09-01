using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PhraseLayer.Core.Audio;

namespace PhraseLayer.MoonshineDecoderParity
{
    internal sealed class ParityCase
    {
        public string Name { get; set; } = string.Empty;
        public int[] Ids { get; set; } = Array.Empty<int>();
        public string Expected { get; set; } = string.Empty;
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: PhraseLayer.MoonshineDecoderParity <decoder.bin> <cases.json>");
                return 2;
            }

            try
            {
                var decoder = new MoonshineBinaryTokenDecoder(File.ReadAllBytes(args[0]));
                var cases = JsonSerializer.Deserialize<List<ParityCase>>(
                    File.ReadAllText(args[1]),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cases == null || cases.Count == 0)
                    throw new InvalidDataException("Moonshine decoder parity fixture is empty.");

                var failures = 0;
                foreach (var item in cases)
                {
                    var actual = decoder.Decode(item.Ids);
                    if (!string.Equals(actual, item.Expected, StringComparison.Ordinal))
                    {
                        failures++;
                        Console.Error.WriteLine(
                            "FAIL {0}: expected={1} actual={2}",
                            item.Name,
                            JsonSerializer.Serialize(item.Expected),
                            JsonSerializer.Serialize(actual));
                    }
                    else
                    {
                        Console.WriteLine("PASS " + item.Name);
                    }
                }

                if (failures != 0)
                {
                    Console.Error.WriteLine("Moonshine decoder parity failures=" + failures);
                    return 1;
                }
                Console.WriteLine("PASS: Moonshine managed decoder parity cases=" + cases.Count);
                return 0;
            }
            catch (Exception exc)
            {
                Console.Error.WriteLine(exc.ToString());
                return 1;
            }
        }
    }
}
