using Legio.Core;
using Legio.Core.Executio;
using Legio.Protocol;
using Xunit;

namespace Legio.Tests;

public class ConsiliumTests
{
    private sealed class DummySystem(int id) : PriorPilus(id)
    {
        protected override void ExecuteBatchLogic(int start, int length) { }
        protected override int GetTotalItemCount() => 0;
    }

    [Fact]
    public void TransitiveResourceHazards_PreserveRegistrationOrder()
    {
        using var legatus = new Legatus(new LegatusConfig { MaxCohorts = 16, ThreadCount = 2 });

        // C0 writes A(0)
        var c0 = new Cohors(0, new DummySystem(0));
        c0.WriteMask.Tollere(0);

        // C1 reads A(0), writes B(1) -> must run AFTER C0 (Phase 1)
        var c1 = new Cohors(1, new DummySystem(1));
        c1.ReadMask.Tollere(0);
        c1.WriteMask.Tollere(1);

        // C2 reads B(1) -> does NOT conflict with C0, but MUST NOT overtake C1 (must be Phase 2!)
        var c2 = new Cohors(2, new DummySystem(2));
        c2.ReadMask.Tollere(1);

        // C3 reads A(0), writes C(2) -> conflicts with C0 (RAW on 0), compatible with C1 (both read 0, disjoint writes) -> Phase 1!
        var c3 = new Cohors(3, new DummySystem(3));
        c3.ReadMask.Tollere(0);
        c3.WriteMask.Tollere(2);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.RegisterCohors(c3);
        legatus.ConstructConsilium();

        Assert.Equal(3, legatus.GradusCount);
        Assert.Equal(0, legatus.GetCohortPhase(0));
        Assert.Equal(1, legatus.GetCohortPhase(1));
        Assert.Equal(2, legatus.GetCohortPhase(2));
        Assert.Equal(1, legatus.GetCohortPhase(3));
    }

    [Fact]
    public void WriteAfterRead_Hazard_EnforcesSeparatePhase()
    {
        using var legatus = new Legatus(new LegatusConfig { MaxCohorts = 8, ThreadCount = 2 });

        // C0 reads resource 5
        var c0 = new Cohors(0, new DummySystem(0));
        c0.ReadMask.Tollere(5);

        // C1 writes resource 5 -> WAR hazard with C0 -> must be Phase 1
        var c1 = new Cohors(1, new DummySystem(1));
        c1.WriteMask.Tollere(5);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.ConstructConsilium();

        Assert.Equal(2, legatus.GradusCount);
        Assert.Equal(0, legatus.GetCohortPhase(0));
        Assert.Equal(1, legatus.GetCohortPhase(1));
    }

    [Fact]
    public void MoreThan32SequentialPhases_DoesNotOverflowConsilium()
    {
        const int count = 48;
        using var legatus = new Legatus(new LegatusConfig { MaxCohorts = count, ThreadCount = 2 });

        for (int i = 0; i < count; i++)
        {
            var c = new Cohors(i, new DummySystem(i));
            c.WriteMask.Tollere(0); // WAW chain -> 48 distinct phases
            legatus.RegisterCohors(c);
        }

        legatus.ConstructConsilium();

        Assert.Equal(count, legatus.GradusCount);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, legatus.GetCohortPhase(i));
        }
    }
}
