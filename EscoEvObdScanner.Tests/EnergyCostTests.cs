using System.Globalization;

namespace EscoEvObdScanner.Tests
{
    /// <summary>The dashboard's lifetime figures: grid energy, cost and the yearly share.</summary>
    public class EnergyCostTests
    {
        [Fact]
        public void CostAddsTheChargingLossToWhatWasTakenOut()
        {
            Assert.Equal(1100, EnergyCost.FromGrid(1000, 10)!.Value, 6);
            Assert.Equal(176, EnergyCost.Euros(1000, 16, 10)!.Value, 6);
            Assert.Equal(160, EnergyCost.Euros(1000, 16, 0)!.Value, 6);
        }

        [Fact]
        public void NoPriceOrNoEnergyGivesNoCost()
        {
            Assert.Null(EnergyCost.Euros(1000, null, 10));
            Assert.Null(EnergyCost.Euros(null, 16, 10));
            Assert.Null(EnergyCost.Euros(1000, -1, 10));
            Assert.Null(EnergyCost.FromGrid(1000, 100));
        }

        [Fact]
        public void AverageNeedsAHundredKilometres()
        {
            Assert.Equal(19.25, EnergyCost.Per100Km(11550, 60000)!.Value, 6);
            Assert.Null(EnergyCost.Per100Km(10, 99));
            Assert.Null(EnergyCost.Per100Km(10, null));
        }

        [Fact]
        public void OwnShareCountsTheKilometresSince()
        {
            Assert.Equal(600, EnergyCost.Since(1200, 60000, 30000)!.Value, 6);
            Assert.Equal(1200, EnergyCost.Since(1200, 60000, 0)!.Value, 6);
            Assert.Null(EnergyCost.Since(1200, 60000, 60000));
            Assert.Null(EnergyCost.Since(1200, 50, 0));
        }

        [Theory]
        [InlineData("29690", 29690)]
        [InlineData("29 690", 29690)]
        [InlineData("29 690", 29690)]
        [InlineData("29,690", 29690)]
        [InlineData("29.690", 29690)]
        [InlineData("1,234,567", 1234567)]
        [InlineData("0", 0)]
        public void OdometerTakenIntoUseReadsThousandsSeparators(string typed, double km) =>
            Assert.Equal(km, AppLogic.ViewModels.LiveViewModel.ParseKm(typed));

        [Theory]
        [InlineData("45,5")]
        [InlineData("45.00")]
        [InlineData("-5")]
        [InlineData("abc")]
        [InlineData("3000000")]
        [InlineData("١٢٣")]         // digits of another script
        public void OdometerTakenIntoUseRefusesWhatIsNoWholeKilometres(string typed) =>
            Assert.Null(AppLogic.ViewModels.LiveViewModel.ParseKm(typed));

        [Theory]
        [InlineData("fi-FI", "4.12.2024", "2024-12-04")]
        [InlineData("fi-FI", " 2024-12-04 ", "2024-12-04")]
        [InlineData("fi-FI", "1.5.23", "2023-05-01")]
        [InlineData("fi-FI", "04.12.2024", "2024-12-04")]
        [InlineData("en-US", "4.12.2024", "2024-12-04")]      // full stops are always day first
        [InlineData("en-US", "2024-12-04", "2024-12-04")]
        [InlineData("en-US", "1.5.23", "2023-05-01")]
        [InlineData("en-US", "1.5.2023", "2023-05-01")]
        [InlineData("en-US", "1/5/2023", "2023-01-05")]       // anything else in the language's own format
        public void DateTakenIntoUseReadsTheFormatsItKnows(string culture, string typed, string expected)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(DateTime.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture), AppLogic.ViewModels.LiveViewModel.ParseDate(typed));
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        [Theory]
        [InlineData("fi-FI", "1.5.")]
        [InlineData("fi-FI", "12.30")]
        [InlineData("fi-FI", "4.12.2024 10:00")]
        [InlineData("fi-FI", "1/5/2023")]
        [InlineData("fi-FI", "2024-12-4")]
        [InlineData("en-US", "1.5.")]
        [InlineData("en-US", "12.30")]
        [InlineData("en-US", "2024-12-04T10:00")]
        [InlineData("en-US", "May 1 2023")]
        public void DateTakenIntoUseRefusesAnythingElse(string culture, string typed)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Null(AppLogic.ViewModels.LiveViewModel.ParseDate(typed));
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        [Fact]
        public void YearlyShareNeedsHalfAYear()
        {
            var made = new DateTime(2022, 10, 11);
            Assert.Equal(500, EnergyCost.PerYear(2000, made, made.AddDays(4 * 365.25))!.Value, 6);
            Assert.Null(EnergyCost.PerYear(2000, made, made.AddDays(100)));
            Assert.Null(EnergyCost.PerYear(2000, null, made));
        }

        [Fact]
        public void StellantisLifetimeCountersDecodeFromTheirReply()
        {
            // 2026-10-11 after a DC charge: words 0 and 1 of 0xD42E at 1/16 kWh
            var catalog = TestData.Catalog();
            byte[] payload = Convert.FromHexString("000336C00002D4D60000EB56");
            var charged = catalog.LiveOf("lifetime_charged_kwh")!;
            var discharged = catalog.LiveOf("lifetime_discharged_kwh")!;
            Assert.Equal(13164.0, Expr.Evaluate(charged.Expression!, payload)!.Value, 6);
            Assert.Equal(11597.375, Expr.Evaluate(discharged.Expression!, payload)!.Value, 6);
            // word 0 reads 0 after the car wakes until it has driven or charged: below the minimum, so no value
            Assert.True(charged.Min > 0);
        }
    }
}
