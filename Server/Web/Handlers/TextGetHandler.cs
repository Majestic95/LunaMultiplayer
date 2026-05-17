using System;
using System.Threading.Tasks;
using uhttpsharp;

namespace Server.Web.Handlers
{
    /// <summary>
    /// Companion to <see cref="JsonGetHandler"/> for endpoints that should
    /// render as plain text in a browser — the admin log tail is the
    /// motivating case. Returns <c>text/plain; charset=utf-8</c>. Non-GET
    /// verbs delegate to the next handler so the framework's default 404/405
    /// behavior still applies.
    /// </summary>
    public sealed class TextGetHandler : IHttpRequestHandler
    {
        private readonly Func<string> _bodyFactory;

        public TextGetHandler(Func<string> bodyFactory)
        {
            _bodyFactory = bodyFactory ?? throw new ArgumentNullException(nameof(bodyFactory));
        }

        public Task Handle(IHttpContext context, Func<Task> next)
        {
            if (context.Request.Method != HttpMethods.Get)
                return next();

            var body = _bodyFactory() ?? string.Empty;
            context.Response = StringHttpResponse.Create(body, HttpResponseCode.Ok, "text/plain; charset=utf-8");
            return Task.CompletedTask;
        }
    }
}
