using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Refresh/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetTuneInRefreshStatus : IReturn<TuneInRefreshStatus> { }

    public sealed class TuneInRefreshService : IService, IRequiresRequest
    {
        private readonly TuneInRefresh refresh;
        public IRequest Request { get; set; }

        public TuneInRefreshService(IHttpClient client, IApplicationHost host)
            : this(TuneInRequests.Refresh(client, host)) { }

        internal TuneInRefreshService(TuneInRefresh refresh) { this.refresh = refresh; }

        public TuneInRefreshStatus Get(GetTuneInRefreshStatus request)
        {
            if (Request == null) throw new UnauthorizedAccessException("TuneIn diagnostics require administrator access.");
            return refresh.Status();
        }
    }
}
