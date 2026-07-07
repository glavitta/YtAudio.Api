using Hangfire.Dashboard;
using System.Net;

namespace YtAudio.Api.Middleware
{
    public class HangfireLocalFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var http = context.GetHttpContext();
            var remote = http.Connection.RemoteIpAddress;

            if (remote is null) return false;

            return IPAddress.IsLoopback(remote);
        }
    }
}
