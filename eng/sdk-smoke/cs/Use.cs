using Sample.Client;
using Sample.Client.Models;
public static class Use
{
    public static async Task<string?> Run(OpenFetchClient client)
    {
        var result = await client.Query.Orders.PostAsync(new OrdersQuery
        {
            Select = [OrdersSelectableField.Id, OrdersSelectableField.CustomerName],
            Page = new OrdersPage { First = 10 },
        });
        return result?.Data?[0].Customer?.Name;
    }
}
