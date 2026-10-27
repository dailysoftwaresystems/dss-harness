namespace DssHarness.Tests;

/// <summary>
/// The fixture repositories the context pack is measured over (part 08, "What the saving is", measurement 1): each a small
/// repository of one language, and the tasks over it, each naming the declarations its reference answer needs. The C#
/// one is the context pack's own (20l); 27d's mechanism test adds a C and a Dart one beside it.
/// </summary>
internal static class ContextPackFixtures
{
    /// <summary>A shop in C#: orders and their lines, discounts behind an interface, a store, a checkout over a payment gateway, a report, and tests of each.</summary>
    public static IReadOnlyList<(string Path, string Text)> CSharp { get; } =
    [
        ("src/Orders/Order.cs", """
            namespace Shop.Orders;

            /// <summary>One order: its lines, and whether it is open.</summary>
            public sealed class Order
            {
                private readonly List<OrderLine> _lines = [];

                public Order(string id, string customer)
                {
                    Id = id;
                    Customer = customer;
                }

                public string Id { get; }

                public string Customer { get; }

                public OrderState State { get; private set; } = OrderState.Open;

                public IReadOnlyList<OrderLine> Lines => _lines;

                public void Add(OrderLine line)
                {
                    if (State != OrderState.Open)
                    {
                        throw new InvalidOperationException("the order is closed");
                    }

                    _lines.Add(line);
                }

                public decimal Total()
                {
                    var total = 0m;

                    foreach (var line in _lines)
                    {
                        total += line.Amount();
                    }

                    return total;
                }

                public void Close()
                {
                    State = OrderState.Closed;
                }
            }

            """),
        ("src/Orders/OrderLine.cs", """
            namespace Shop.Orders;

            /// <summary>One line of an order: what was bought, how many, at what price.</summary>
            public sealed record OrderLine(string Sku, int Quantity, decimal Price)
            {
                public decimal Amount() => Quantity * Price;
            }

            """),
        ("src/Orders/OrderState.cs", """
            namespace Shop.Orders;

            public enum OrderState
            {
                Open,
                Closed,
            }

            """),
        ("src/Pricing/IDiscount.cs", """
            using Shop.Orders;

            namespace Shop.Pricing;

            /// <summary>A discount taken off an order's total.</summary>
            public interface IDiscount
            {
                decimal Apply(Order order, decimal total);
            }

            """),
        ("src/Pricing/LoyaltyDiscount.cs", """
            using Shop.Orders;

            namespace Shop.Pricing;

            /// <summary>Five percent off for a customer who has ordered before.</summary>
            public sealed class LoyaltyDiscount(ICustomerHistory history) : IDiscount
            {
                public decimal Apply(Order order, decimal total)
                {
                    return history.HasOrdered(order.Customer) ? total * 0.95m : total;
                }
            }

            """),
        ("src/Pricing/SeasonalDiscount.cs", """
            using Shop.Orders;

            namespace Shop.Pricing;

            /// <summary>Ten off an order over a hundred, in the season.</summary>
            public sealed class SeasonalDiscount(bool inSeason) : IDiscount
            {
                public decimal Apply(Order order, decimal total)
                {
                    return inSeason && total > 100m ? total - 10m : total;
                }
            }

            """),
        ("src/Pricing/ICustomerHistory.cs", """
            namespace Shop.Pricing;

            /// <summary>What a customer ordered before.</summary>
            public interface ICustomerHistory
            {
                bool HasOrdered(string customer);
            }

            """),
        ("src/Pricing/PriceCalculator.cs", """
            using Shop.Orders;

            namespace Shop.Pricing;

            /// <summary>An order's price: its total with each discount taken off, rounded to the cent.</summary>
            public sealed class PriceCalculator(IReadOnlyList<IDiscount> discounts)
            {
                public decimal Price(Order order)
                {
                    var total = order.Total();

                    foreach (var discount in discounts)
                    {
                        total = discount.Apply(order, total);
                    }

                    return Round(total);
                }

                private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
            }

            """),
        ("src/Storage/IOrderStore.cs", """
            using Shop.Orders;

            namespace Shop.Storage;

            /// <summary>Where orders are kept.</summary>
            public interface IOrderStore
            {
                Order? Find(string id);

                void Save(Order order);

                IReadOnlyList<Order> All();
            }

            """),
        ("src/Storage/MemoryOrderStore.cs", """
            using Shop.Orders;

            namespace Shop.Storage;

            /// <summary>Orders kept in memory, by id.</summary>
            public sealed class MemoryOrderStore : IOrderStore
            {
                private readonly Dictionary<string, Order> _orders = [];

                public Order? Find(string id) => _orders.GetValueOrDefault(id);

                public void Save(Order order)
                {
                    _orders[order.Id] = order;
                }

                public IReadOnlyList<Order> All() => [.. _orders.Values];
            }

            """),
        ("src/Checkout/IPaymentGateway.cs", """
            namespace Shop.Checkout;

            /// <summary>Takes a payment.</summary>
            public interface IPaymentGateway
            {
                string Charge(string customer, decimal amount);
            }

            """),
        ("src/Checkout/RetryingGateway.cs", """
            namespace Shop.Checkout;

            /// <summary>A gateway that tries again, up to a number of times, where a payment times out.</summary>
            public sealed class RetryingGateway(IPaymentGateway inner, int attempts) : IPaymentGateway
            {
                public string Charge(string customer, decimal amount)
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            return inner.Charge(customer, amount);
                        }
                        catch (TimeoutException) when (attempt < attempts)
                        {
                        }
                    }
                }
            }

            """),
        ("src/Checkout/Receipt.cs", """
            namespace Shop.Checkout;

            /// <summary>What a checkout gives back: the order, what was paid, and the payment's reference.</summary>
            public sealed record Receipt(string OrderId, decimal Paid, string Reference);

            """),
        ("src/Checkout/CheckoutService.cs", """
            using Shop.Orders;
            using Shop.Pricing;
            using Shop.Storage;

            namespace Shop.Checkout;

            /// <summary>Checks an order out: prices it, charges the customer, and closes it.</summary>
            public sealed class CheckoutService(IOrderStore store, PriceCalculator calculator, IPaymentGateway gateway)
            {
                public Receipt Checkout(string orderId)
                {
                    var order = store.Find(orderId) ?? throw new InvalidOperationException($"no order '{orderId}'");
                    var price = calculator.Price(order);
                    var reference = gateway.Charge(order.Customer, price);
                    order.Close();
                    store.Save(order);
                    return new Receipt(order.Id, price, reference);
                }
            }

            """),
        ("src/Reports/SalesReport.cs", """
            using Shop.Orders;
            using Shop.Pricing;
            using Shop.Storage;

            namespace Shop.Reports;

            /// <summary>What the closed orders came to.</summary>
            public sealed class SalesReport(IOrderStore store, PriceCalculator calculator)
            {
                public decimal Sales()
                {
                    var sales = 0m;

                    foreach (var order in store.All())
                    {
                        if (order.State == OrderState.Closed)
                        {
                            sales += calculator.Price(order);
                        }
                    }

                    return sales;
                }
            }

            """),
        ("tests/Shop.Tests/OrderTests.cs", """
            using Shop.Orders;
            using Xunit;

            namespace Shop.Tests;

            public sealed class OrderTests
            {
                [Fact]
                public void Total_AddsEveryLine()
                {
                    var order = new Order("o1", "ann");
                    order.Add(new OrderLine("a", 2, 3m));
                    order.Add(new OrderLine("b", 1, 4m));
                    Assert.Equal(10m, order.Total());
                }

                [Fact]
                public void Add_ToAClosedOrder_Throws()
                {
                    var order = new Order("o1", "ann");
                    order.Close();
                    Assert.Throws<InvalidOperationException>(() => order.Add(new OrderLine("a", 1, 1m)));
                }
            }

            """),
        ("tests/Shop.Tests/PriceCalculatorTests.cs", """
            using Shop.Orders;
            using Shop.Pricing;
            using Xunit;

            namespace Shop.Tests;

            public sealed class PriceCalculatorTests
            {
                [Fact]
                public void Price_TakesEachDiscountOff()
                {
                    var order = new Order("o1", "ann");
                    order.Add(new OrderLine("a", 1, 200m));
                    var calculator = new PriceCalculator([new SeasonalDiscount(true)]);
                    Assert.Equal(190m, calculator.Price(order));
                }
            }

            """),
        ("tests/Shop.Tests/LoyaltyDiscountTests.cs", """
            using Shop.Orders;
            using Shop.Pricing;
            using Xunit;

            namespace Shop.Tests;

            public sealed class LoyaltyDiscountTests
            {
                private sealed class Returning : ICustomerHistory
                {
                    public bool HasOrdered(string customer) => true;
                }

                [Fact]
                public void Apply_TakesFivePercentOff_ForACustomerWhoOrderedBefore()
                {
                    var discount = new LoyaltyDiscount(new Returning());
                    Assert.Equal(95m, discount.Apply(new Order("o1", "ann"), 100m));
                }
            }

            """),
        ("tests/Shop.Tests/CheckoutServiceTests.cs", """
            using Shop.Checkout;
            using Shop.Orders;
            using Shop.Pricing;
            using Shop.Storage;
            using Xunit;

            namespace Shop.Tests;

            public sealed class CheckoutServiceTests
            {
                private sealed class Paying : IPaymentGateway
                {
                    public string Charge(string customer, decimal amount) => "paid";
                }

                [Fact]
                public void Checkout_ChargesThePriceAndClosesTheOrder()
                {
                    var store = new MemoryOrderStore();
                    var order = new Order("o1", "ann");
                    order.Add(new OrderLine("a", 1, 10m));
                    store.Save(order);
                    var service = new CheckoutService(store, new PriceCalculator([]), new Paying());
                    var receipt = service.Checkout("o1");
                    Assert.Equal("paid", receipt.Reference);
                    Assert.Equal(OrderState.Closed, order.State);
                }
            }

            """),
        ("README.md", """
            # Shop

            Orders, their pricing, and their checkout.

            """),
    ];

    /// <summary>A cap a task's whole pack does not fit, so what its ranking puts first is what is held.</summary>
    public const int TightTokens = 300;

    /// <summary>
    /// The tasks over <see cref="CSharp"/>: each one's text, what its pack starts from, the change the fixture applies first
    /// for one that starts from the work's changes, the declarations its reference answer needs by qualified name, and how
    /// many of them its pack holds, at the default cap and under <see cref="TightTokens"/> - stored, so a change that packs
    /// less of what a task needs is red, and one that packs more is a count to raise.
    /// </summary>
    public static IReadOnlyList<ContextPackTask> CSharpTasks { get; } =
    [
        new(
            "gift-card",
            "Add a gift-card discount: a new `IDiscount` that takes the card's balance off the total, applied by `PriceCalculator.Price` with the others.",
            Change: null,
            ["Shop.Pricing.IDiscount", "Shop.Pricing.IDiscount.Apply", "Shop.Pricing.PriceCalculator.Price", "Shop.Pricing.LoyaltyDiscount.Apply", "Shop.Pricing.SeasonalDiscount.Apply", "Shop.Tests.PriceCalculatorTests.Price_TakesEachDiscountOff"],
            Held: 6, HeldTight: 2),
        new(
            "gateway-timeout",
            """
            Checkout fails when the payment gateway times out:

            System.TimeoutException: the gateway did not answer
               at Shop.Checkout.CheckoutService.Checkout(String orderId) in /repo/src/Checkout/CheckoutService.cs:line 14

            Retry the charge before giving up.
            """,
            Change: null,
            ["Shop.Checkout.CheckoutService.Checkout", "Shop.Checkout.IPaymentGateway.Charge", "Shop.Checkout.RetryingGateway.Charge", "Shop.Tests.CheckoutServiceTests.Checkout_ChargesThePriceAndClosesTheOrder"],
            Held: 4, HeldTight: 2),
        new(
            "shipping-fee",
            "Make Order.Total include a flat shipping fee for an order of fewer than three lines.",
            Change: null,
            ["Shop.Orders.Order.Total", "Shop.Orders.OrderLine.Amount", "Shop.Pricing.PriceCalculator.Price", "Shop.Tests.OrderTests.Total_AddsEveryLine"],
            Held: 4, HeldTight: 3),
        new(
            "review-loyalty",
            "Review the change to the loyalty discount.",
            Change: ("src/Pricing/LoyaltyDiscount.cs", "total * 0.95m", "total * 0.90m"),
            ["Shop.Pricing.LoyaltyDiscount.Apply", "Shop.Tests.LoyaltyDiscountTests.Apply_TakesFivePercentOff_ForACustomerWhoOrderedBefore", "Shop.Pricing.IDiscount.Apply"],
            Held: 3, HeldTight: 2),
        new(
            "review-total",
            "Review the change.",
            Change: ("src/Orders/Order.cs", "total += line.Amount();", "total += line.Amount() * 1;"),
            ["Shop.Orders.Order.Total", "Shop.Tests.OrderTests.Total_AddsEveryLine", "Shop.Pricing.PriceCalculator.Price", "Shop.Tests.PriceCalculatorTests.Price_TakesEachDiscountOff", "Shop.Checkout.CheckoutService.Checkout", "Shop.Reports.SalesReport.Sales"],
            Held: 6, HeldTight: 5),
    ];
}

/// <summary>One task over a fixture repository.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Text">Its text: the step's filled prompt.</param>
/// <param name="Change">The change the fixture applies before the pack is made - a file, the text it replaces and the text it puts - for a task that starts from the work's changes; null for one that starts from the task.</param>
/// <param name="Needed">The declarations its reference answer needs, by qualified name.</param>
/// <param name="Held">How many of them its pack holds.</param>
/// <param name="HeldTight">How many of them its pack holds under <see cref="ContextPackFixtures.TightTokens"/>, where the ranking decides.</param>
internal sealed record ContextPackTask(string Name, string Text, (string Path, string Old, string New)? Change, IReadOnlyList<string> Needed, int Held, int HeldTight);
