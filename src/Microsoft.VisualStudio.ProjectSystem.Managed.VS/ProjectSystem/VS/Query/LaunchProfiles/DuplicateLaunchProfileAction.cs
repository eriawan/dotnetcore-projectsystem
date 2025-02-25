﻿// Licensed to the .NET Foundation under one or more agreements. The .NET Foundation licenses this file to you under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ProjectSystem.Query;
using Microsoft.VisualStudio.ProjectSystem.Query.ProjectModelMethods.Actions;
using Microsoft.VisualStudio.ProjectSystem.Query.QueryExecution;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Query
{
    /// <summary>
    /// <see cref="IQueryActionExecutor"/> handling <see cref="ProjectModelActionNames.DuplicateLaunchProfile"/> actions.
    /// </summary>
    internal sealed class DuplicateLaunchProfileAction : LaunchProfileActionBase
    {
        private readonly DuplicateLaunchProfile _executableStep;

        public DuplicateLaunchProfileAction(DuplicateLaunchProfile executableStep)
        {
            _executableStep = executableStep;
        }

        protected override async Task ExecuteAsync(IQueryExecutionContext queryExecutionContext, IEntityValue projectEntity, IProjectLaunchProfileHandler launchProfileHandler, CancellationToken cancellationToken)
        {
            EntityIdentity? newLaunchProfileId = await launchProfileHandler.DuplicateLaunchProfileAsync(queryExecutionContext, projectEntity, _executableStep.CurrentProfileName, _executableStep.NewProfileName, _executableStep.NewProfileCommandName, cancellationToken);

            if (newLaunchProfileId is not null)
            {
                AddedLaunchProfiles.Add((projectEntity, newLaunchProfileId));
            }
        }
    }
}
