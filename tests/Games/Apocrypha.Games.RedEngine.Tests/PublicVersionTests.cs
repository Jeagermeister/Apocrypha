using Apocrypha.Games.RedEngine.Cyberpunk2077;
using Xunit;
namespace Apocrypha.Games.RedEngine.Tests;
public class PublicVersionTests
{
    [Fact] public void KnownExecutableHasPublicVersion() => Assert.Equal("2.5", CyberpunkPublicVersion.FromExecutableHash("d36e74c47592e9c656ae250078cb40b2be63c782af5bfbeff2280d43e653fab4"));
    [Theory] [InlineData("")] [InlineData("26161058")] [InlineData("3524634124112875626")] [InlineData("3.0.5813403")] [InlineData("unknown")]
    public void BuildManifestInternalVersionAndMissingIdentityAreNotPublicVersion(string identity) => Assert.Null(CyberpunkPublicVersion.FromExecutableHash(identity));
}
