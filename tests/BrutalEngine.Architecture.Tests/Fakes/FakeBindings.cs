// Stand-ins for BRUTAL native binding types, so BindingSurfaceRule is tested without a real binding package.
namespace Brutal.FakeApi;

public struct FakeDevice
{
    public int Value { get; set; }
}

public interface IFakeCallbacks
{
}

public class FakeBase
{
}

public delegate void FakeCallback(FakeDevice device);
