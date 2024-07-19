using Application.Common;
using Application.LocalStore.Services;
using Domain.Runner.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TransactionHelpers;
using TransactionHelpers.Interface;

namespace Application.Runner.Services;

public class RunnerStoreService(ILogger<RunnerStoreService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<RunnerStoreService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private const string _edgeGroupStore = "runner_group_store";

    private LocalStoreService? _localStoreService = null;

    public LocalStoreService GetStore()
    {
        _localStoreService ??= _serviceProvider.GetRequiredService<LocalStoreService>();
        _localStoreService.CommonGroup = _edgeGroupStore;
        return _localStoreService;
    }
}
