using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

public class OppositeHandAlignmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_logged_volume_mismatch_requires_independent_geometric_proof(bool coincides)
    {
        const double seedVolumeM3 = 1551.791e-9;
        const double copyVolumeM3 = 1551.918e-9;

        bool accepted = AssemblyService.TryVerifyOppositeHandVolume(seedVolumeM3, copyVolumeM3, () => coincides, out bool usedCoincidence);

        Assert.Equal(coincides, accepted);
        Assert.True(usedCoincidence);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void A_coincidence_transform_must_match_the_requested_reflection(int candidate, bool matches)
    {
        Vec3 Expected(Vec3 p) => new(0.07 - p.X, p.Y + 0.02, p.Z - 0.01);
        Func<Vec3, Vec3> alignment = candidate switch
        {
            0 => p => new(0.07 - p.X, p.Y + 0.02, p.Z - 0.01),
            1 => p => new(0.07 + p.X, p.Y + 0.02, p.Z - 0.01),
            _ => p => new(0.09 - p.X, p.Y + 0.02, p.Z - 0.01),
        };

        Assert.Equal(matches, AssemblyService.OppositeHandAlignmentMatches(Expected, alignment));
    }
}
