using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SetupLocalizationContractTests
    {
        private static readonly Regex PlaceholderRegex =
            new Regex(@"\{\d+(?:[^}]*)\}", RegexOptions.Compiled);

        [Fact]
        public void Setup_catalogs_keep_key_value_and_placeholder_parity()
        {
            Dictionary<string, string> english = Dictionary("En");
            Assert.NotEmpty(english);

            foreach (string field in new[] { "It", "Es", "Zh" })
            {
                Dictionary<string, string> localized = Dictionary(field);
                Assert.Equal(english.Keys.OrderBy(key => key), localized.Keys.OrderBy(key => key));

                foreach (KeyValuePair<string, string> entry in english)
                {
                    string value = localized[entry.Key];
                    Assert.False(string.IsNullOrWhiteSpace(value), field + "/" + entry.Key + " is empty");
                    Assert.Equal(Placeholders(entry.Value), Placeholders(value));
                }
            }
        }

        [Fact]
        public void Setup_unknown_keys_fall_back_to_the_key()
        {
            I18n.Initialize("en");
            Assert.Equal("missing_contract_key", I18n.T("missing_contract_key"));
        }

        private static Dictionary<string, string> Dictionary(string field)
            => (Dictionary<string, string>)typeof(I18n)
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

        private static string[] Placeholders(string value)
            => PlaceholderRegex.Matches(value)
                .Cast<Match>()
                .Select(match => match.Value)
                .OrderBy(value => value)
                .ToArray();
    }
}
