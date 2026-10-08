using System.Net;
using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Exceptions;
using Microsoft.Extensions.Options;
using Xunit;

namespace WireCabinet.Tests;

public class AgvDispatchResponseTests
{
    [Theory]
    [InlineData("{\"code\":\"1001\",\"message\":\"拒绝取消\"}")]
    [InlineData("{\"code\":1001,\"message\":\"拒绝取消\"}")]
    public async Task Http200BusinessFailureIsNotReportedAsSuccessfulCancellation(string response)
    {
        using var http = new HttpClient(new Responses(response));
        using var client = new AgvDispatchClient(http, Options.Create(new AgvDispatchOptions { BaseUrl = "http://offline.invalid", Username = "fake", Password = "fake" }));
        var error = await Assert.ThrowsAsync<AgvDispatchException>(() => client.CancelOrderAsync("order"));
        Assert.Contains("拒绝取消", error.Message);
    }

    [Fact]
    public async Task SnakeCaseCompletionTimeIsAvailableToPendingOrderCleanup()
    {
        using var http = new HttpClient(new Responses("{\"code\":\"0\",\"result\":{\"order_state\":2,\"done_time\":\"2026-10-08 08:00:00\"}}"));
        using var client = new AgvDispatchClient(http, Options.Create(new AgvDispatchOptions { BaseUrl = "http://offline.invalid" }));
        var detail = await client.GetOrderDetailAsync("order");
        Assert.Equal("2026-10-08 08:00:00", detail.EffectiveDoneTime);
    }

    [Fact]
    public async Task MissingOrderResultCannotBecomeAnEmptySuccessfulOrderDetail()
    {
        using var http = new HttpClient(new Responses("{\"code\":\"0\",\"result\":null}"));
        using var client = new AgvDispatchClient(http, Options.Create(new AgvDispatchOptions { BaseUrl = "http://offline.invalid" }));
        await Assert.ThrowsAsync<AgvDispatchException>(() => client.GetOrderDetailAsync("order"));
    }

    private sealed class Responses(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("login")
                    ? "{\"code\":\"0\",\"result\":{\"token\":\"fake-token\"}}" : response)
            });
    }
}
