using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;

namespace HexTail.Tests.Application;

public sealed class FileTabStateTests
{
    [Fact]
    public async Task EffectiveRules_AreCachedUntilGlobalOrSourceRulesChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hextail-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "error\n", TestContext.Current.CancellationToken);
        await using var state = new AppState(
            new LogSourceService(
                new TailerOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    UseFileSystemWatcher = false,
                }
            ),
            new TestPersistence(),
            credentials: new InMemoryCredentialVault()
        );
        try
        {
            var tab = await state.OpenFileAsync(
                path,
                save: false,
                cancellationToken: TestContext.Current.CancellationToken
            );
            var settings = new AppSettings { Rules = [TestRules.Hide("noise")] };

            var rules = tab.EffectiveRules(settings);
            Assert.Same(rules, tab.EffectiveRules(settings));
            Assert.Same(
                rules,
                tab.EffectiveRules(settings with { LogFontSize = LogFontSize.Large })
            );

            var versionBefore = tab.RulesVersion;
            tab.SetRules([TestRules.Highlight("error") with { Scope = RuleScope.Source }]);
            Assert.Equal(versionBefore + 1, tab.RulesVersion);
            var withSource = tab.EffectiveRules(settings);
            Assert.NotSame(rules, withSource);
            Assert.Same(withSource, tab.EffectiveRules(settings));

            var changedGlobal = tab.EffectiveRules(
                settings with
                {
                    Rules = [TestRules.Hide("other")],
                }
            );
            Assert.NotSame(withSource, changedGlobal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
