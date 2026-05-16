using System;
using System.Threading.Tasks;
using uhttpsharp;
using uhttpsharp.Handlers;

namespace Server.Web.Handlers
{
    /// <summary>
    /// Lightweight read-only JSON endpoint. Built for the fork's admin dashboard
    /// (Stage 3.7) where the existing <see cref="RestHandler{T}"/> +
    /// <see cref="uhttpsharp.Handlers.IRestController{T}"/> pair adds five
    /// per-endpoint methods of MethodNotAllowed boilerplate that we don't need.
    /// Non-GET verbs delegate to the next handler so the framework's default
    /// 404/405 handling still applies.
    /// </summary>
    public sealed class JsonGetHandler : IHttpRequestHandler
    {
        private readonly Func<object> _payloadFactory;

        public JsonGetHandler(Func<object> payloadFactory)
        {
            _payloadFactory = payloadFactory ?? throw new ArgumentNullException(nameof(payloadFactory));
        }

        public async Task Handle(IHttpContext context, Func<Task> next)
        {
            if (context.Request.Method != HttpMethods.Get)
            {
                await next().ConfigureAwait(false);
                return;
            }

            var payload = _payloadFactory();
            context.Response = await JsonResponseProvider.Default.Provide(payload).ConfigureAwait(false);
        }
    }
}
