using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Selectors", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetTuneInSelectors : IReturn<SelectorResult>
    {
        public string Category { get; set; }
        public string Parent { get; set; }
        public bool Refresh { get; set; }
    }

    public class TuneInSelectorService : IService, IRequiresRequest
    {
        private readonly TuneInDirectory directory;
        public IRequest Request { get; set; }

        public TuneInSelectorService(IHttpClient client, IApplicationHost host)
            : this(TuneInRequests.Directory(client, host)) { }

        internal TuneInSelectorService(TuneInDirectory directory) { this.directory = directory; }

        public Task<SelectorResult> Get(GetTuneInSelectors request)
        {
            if (Request == null)
                throw new UnauthorizedAccessException("TuneIn selectors require administrator access.");
            return directory.SelectAsync(request.Category, request.Parent, request.Refresh, Request.CancellationToken);
        }
    }
}
