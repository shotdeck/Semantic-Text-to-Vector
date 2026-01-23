using Xunit;

namespace ShotDeckSearch.Tests
{
    public class DummyPipelineTests
    {
        [Fact(DisplayName = "Force fail to block build")]
        public void PipelineShouldFail()
        {
            Assert.True(false, "Simulated pipeline failure from xUnit test.");
            //Assert.True(true);
        }
    }
}