using EscoEvObdScanner.AppLogic.Infrastructure;

namespace EscoEvObdScanner.Tests
{
    /// <summary>Every Bluetooth LE failure is put in words in the UI language, naming the adapter.</summary>
    public class BleErrorsTests
    {
        [Theory]
        [InlineData(BleProblem.NotFound)]
        [InlineData(BleProblem.PairingFailed)]
        [InlineData(BleProblem.NoService)]
        [InlineData(BleProblem.Refused)]
        [InlineData(BleProblem.NoAnswer)]
        public void EveryProblemHasAText(BleProblem problem)
        {
            var ex = new BleException(problem, "OBDLink CX", "status 3");
            string text = Errors.Describe(ex);

            Assert.Contains("OBDLink CX", text);
            Assert.NotEqual(ex.Message, text);
        }
    }
}
