using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;

namespace CgShop.UnitTests.Domain;

public class OrderTests
{
    [Fact]
    public void New_orders_start_pending_payment_validation_with_correct_totals()
    {
        var orders = TestData.Orders().ToList();

        orders.Should().HaveCount(TestData.BatchSize);
        orders.Should().AllSatisfy(o =>
        {
            o.Status.Should().Be(OrderStatus.PendingPaymentValidation);
            o.IsPaid.Should().BeFalse();
            o.Subtotal.Should().Be(o.Items.Sum(i => i.UnitPrice * i.Quantity));
            o.Tax.Should().Be(Math.Round(o.Subtotal * 0.18m, 2, MidpointRounding.AwayFromZero));
            o.Total.Should().Be(o.Subtotal + o.Tax);
            o.ReservationExpiresAtUtc.Should().Be(TestData.Now.AddHours(48));
            o.History.Should().ContainSingle(h => h.ToStatus == OrderStatus.PendingPaymentValidation);
            o.AccessToken.Should().HaveLength(32);
        });
        orders.Select(o => o.AccessToken).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Tenant_admin_validates_payment_of_100_orders_and_audit_is_recorded()
    {
        foreach (var order in TestData.Orders())
        {
            order.AttachReceipt("comprobante.pdf", "t/receipts/x.pdf", "application/pdf", 1024, "REF-1", TestData.Now);

            order.ValidatePayment(TestData.TenantAdmin, "Transferencia confirmada en banco", TestData.Now.AddHours(1));

            order.Status.Should().Be(OrderStatus.PaymentValidated);
            order.IsPaid.Should().BeTrue();
            order.PaymentValidatedBy.Should().Be(TestData.TenantAdmin.DisplayName);
            var last = order.History[^1];
            last.FromStatus.Should().Be(OrderStatus.PendingPaymentValidation);
            last.ToStatus.Should().Be(OrderStatus.PaymentValidated);
            last.ChangedByUserId.Should().Be(TestData.TenantAdmin.UserId);
            last.ReceiptId.Should().Be(order.Receipts[0].Id);
        }
    }

    [Theory]
    [MemberData(nameof(NonAdminActors))]
    public void Order_cannot_be_approved_without_owner_validation(ActorInfo actor)
    {
        foreach (var order in TestData.Orders())
        {
            var act = () => order.ValidatePayment(actor, null, TestData.Now);

            act.Should().Throw<DomainException>().WithMessage("*propietario*");
            order.Status.Should().Be(OrderStatus.PendingPaymentValidation);
        }
    }

    public static TheoryData<ActorInfo> NonAdminActors() =>
        [TestData.TenantStaff, TestData.Customer, TestData.SuperAdmin, ActorInfo.System];

    [Fact]
    public void Order_cannot_be_prepared_shipped_or_delivered_before_payment_validation()
    {
        foreach (var order in TestData.Orders())
        {
            order.Invoking(o => o.StartPreparing(TestData.TenantAdmin, TestData.Now)).Should().Throw<DomainException>();
            order.Invoking(o => o.Ship(TestData.TenantAdmin, null, TestData.Now)).Should().Throw<DomainException>();
            order.Invoking(o => o.MarkDelivered(TestData.TenantAdmin, TestData.Now)).Should().Throw<DomainException>();
            order.Status.Should().Be(OrderStatus.PendingPaymentValidation);
        }
    }

    [Fact]
    public void Full_lifecycle_after_validation_reaches_delivered()
    {
        foreach (var order in TestData.Orders())
        {
            order.ValidatePayment(TestData.TenantAdmin, null, TestData.Now);
            order.StartPreparing(TestData.TenantStaff, TestData.Now);
            order.Ship(TestData.TenantStaff, "Guía 123", TestData.Now);
            order.MarkDelivered(TestData.TenantStaff, TestData.Now);

            order.Status.Should().Be(OrderStatus.Delivered);
            order.History.Select(h => h.ToStatus).Should().Equal(
                OrderStatus.PendingPaymentValidation, OrderStatus.PaymentValidated, OrderStatus.Preparing,
                OrderStatus.Shipped, OrderStatus.Delivered);
        }
    }

    [Fact]
    public void Rejected_payment_is_terminal_and_requires_reason()
    {
        foreach (var order in TestData.Orders())
        {
            order.Invoking(o => o.RejectPayment(TestData.TenantAdmin, " ", TestData.Now))
                .Should().Throw<DomainException>().WithMessage("*motivo*");

            order.RejectPayment(TestData.TenantAdmin, "Monto no coincide", TestData.Now);

            order.Status.Should().Be(OrderStatus.PaymentRejected);
            order.Invoking(o => o.ValidatePayment(TestData.TenantAdmin, null, TestData.Now))
                .Should().Throw<DomainException>().WithMessage("*Transición inválida*");
        }
    }

    [Fact]
    public void Receipts_can_only_be_attached_while_pending()
    {
        foreach (var order in TestData.Orders())
        {
            order.AttachReceipt("a.png", "p", "image/png", 10, null, TestData.Now);
            order.ValidatePayment(TestData.TenantAdmin, null, TestData.Now);

            order.Invoking(o => o.AttachReceipt("b.png", "p", "image/png", 10, null, TestData.Now))
                .Should().Throw<DomainException>();
        }
    }

    [Fact]
    public void Expire_only_after_reservation_deadline()
    {
        foreach (var order in TestData.Orders())
        {
            order.Invoking(o => o.Expire(TestData.Now.AddHours(47))).Should().Throw<DomainException>();
            order.Expire(TestData.Now.AddHours(48));
            order.Status.Should().Be(OrderStatus.Expired);
            order.History[^1].ChangedByUserId.Should().Be("system");
        }
    }

    [Fact]
    public void Customer_cannot_cancel_but_staff_can()
    {
        foreach (var order in TestData.Orders())
        {
            order.Invoking(o => o.Cancel(TestData.Customer, "Ya no lo quiero", TestData.Now))
                .Should().Throw<DomainException>();
            order.Cancel(TestData.TenantStaff, "Solicitud del cliente", TestData.Now);
            order.Status.Should().Be(OrderStatus.Cancelled);
        }
    }

    [Fact]
    public void Place_merges_duplicate_variant_lines_and_validates_input()
    {
        var product = TestData.Product(1);
        var v = product.Variants[0];
        var order = Order.Place("ORD-1", TestData.CustomerInfo(1),
            [TestData.LineFor(v, product, 2), TestData.LineFor(v, product, 3)],
            PaymentMethod.BankTransfer, 0.18m, "DOP", TestData.Now, TimeSpan.FromHours(1));

        order.Items.Should().ContainSingle().Which.Quantity.Should().Be(5);

        var noLines = () => Order.Place("ORD-2", TestData.CustomerInfo(1), [], PaymentMethod.BankTransfer, 0.18m,
            "DOP", TestData.Now, TimeSpan.FromHours(1));
        noLines.Should().Throw<DomainException>();

        var badEmail = () => Order.Place("ORD-3", TestData.CustomerInfo(1) with { Email = "sin-arroba" },
            [TestData.LineFor(v, product, 1)], PaymentMethod.BankTransfer, 0.18m, "DOP", TestData.Now,
            TimeSpan.FromHours(1));
        badEmail.Should().Throw<DomainException>();
    }
}
