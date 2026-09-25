using CgShop.Domain.Orders;

namespace CgShop.UnitTests.Domain;

public class OrderStateMachineTests
{
    [Theory]
    [InlineData(OrderStatus.PendingPaymentValidation, OrderStatus.PaymentValidated)]
    [InlineData(OrderStatus.PendingPaymentValidation, OrderStatus.PaymentRejected)]
    [InlineData(OrderStatus.PendingPaymentValidation, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.PendingPaymentValidation, OrderStatus.Expired)]
    [InlineData(OrderStatus.PaymentValidated, OrderStatus.Preparing)]
    [InlineData(OrderStatus.Preparing, OrderStatus.Shipped)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Delivered)]
    public void Allows_valid_transitions(OrderStatus from, OrderStatus to) =>
        OrderStateMachine.CanTransition(from, to).Should().BeTrue();

    [Theory]
    [InlineData(OrderStatus.PendingPaymentValidation, OrderStatus.Preparing)]
    [InlineData(OrderStatus.PendingPaymentValidation, OrderStatus.Shipped)]
    [InlineData(OrderStatus.PendingPaymentValidation, OrderStatus.Delivered)]
    [InlineData(OrderStatus.PaymentRejected, OrderStatus.PaymentValidated)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.PaymentValidated)]
    [InlineData(OrderStatus.Expired, OrderStatus.PaymentValidated)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Cancelled)]
    public void Rejects_invalid_transitions(OrderStatus from, OrderStatus to) =>
        OrderStateMachine.CanTransition(from, to).Should().BeFalse();

    [Fact]
    public void Only_path_out_of_pending_into_paid_states_is_PaymentValidated()
    {
        OrderStateMachine.NextStatuses(OrderStatus.PendingPaymentValidation)
            .Intersect(OrderStateMachine.PaidStatuses)
            .Should().BeEquivalentTo([OrderStatus.PaymentValidated]);
    }

    [Theory]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.PaymentRejected)]
    public void Terminal_states_have_no_exit(OrderStatus status) =>
        OrderStateMachine.IsTerminal(status).Should().BeTrue();
}
