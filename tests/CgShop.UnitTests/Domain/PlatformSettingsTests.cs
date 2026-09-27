using CgShop.Domain.Common;
using CgShop.Domain.Platform;
using CgShop.Tests.Shared;

namespace CgShop.UnitTests.Domain;

public class PlatformSettingsTests
{
    private static readonly PlatformSettingsValues Valid = new(48, 10, 5, 5, 10, 300, 6000, 15, 60);

    [Fact]
    public void Accepts_100_valid_configurations()
    {
        for (var i = 1; i <= TestData.BatchSize; i++)
        {
            var values = Valid with
            {
                ReservationHours = i, MaxImagesPerProduct = 1 + i % 50, IdleTimeoutMinutes = 1 + i,
                SessionWarningSeconds = 10 + i % 50
            };
            values.Validate().Should().BeEmpty();

            var settings = PlatformSettings.Create(Valid);
            settings.Update(values, TestData.SuperAdmin, TestData.Now);
            settings.ToValues().Should().Be(values);
            settings.UpdatedBy.Should().Be(TestData.SuperAdmin.DisplayName);
        }
    }

    [Theory]
    [InlineData("ReservationHours", 0)]
    [InlineData("ReservationHours", 721)]
    [InlineData("MaxReceiptMb", 0)]
    [InlineData("MaxImageMb", 51)]
    [InlineData("MaxImagesPerProduct", 0)]
    [InlineData("IdleTimeoutMinutes", 0)]
    [InlineData("SessionWarningSeconds", 5)]
    public void Rejects_out_of_range_values(string field, int value)
    {
        var values = field switch
        {
            "ReservationHours" => Valid with { ReservationHours = value },
            "MaxReceiptMb" => Valid with { MaxReceiptMb = value },
            "MaxImageMb" => Valid with { MaxImageMb = value },
            "MaxImagesPerProduct" => Valid with { MaxImagesPerProduct = value },
            "IdleTimeoutMinutes" => Valid with { IdleTimeoutMinutes = value },
            _ => Valid with { SessionWarningSeconds = value }
        };

        values.Validate().Should().NotBeEmpty();
        FluentActions.Invoking(() => PlatformSettings.Create(values)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Rejects_inconsistent_values()
    {
        (Valid with { MinImageDimension = 5000, MaxImageDimension = 1000 }).Validate()
            .Should().Contain(e => e.Contains("mínima"));
        (Valid with { IdleTimeoutMinutes = 1, SessionWarningSeconds = 60 }).Validate()
            .Should().Contain(e => e.Contains("aviso"));
    }

    [Fact]
    public void Only_super_admin_can_update()
    {
        var settings = PlatformSettings.Create(Valid);
        settings.Invoking(s => s.Update(Valid, TestData.TenantAdmin, TestData.Now))
            .Should().Throw<DomainException>().WithMessage("*Super Admin*");
    }
}
