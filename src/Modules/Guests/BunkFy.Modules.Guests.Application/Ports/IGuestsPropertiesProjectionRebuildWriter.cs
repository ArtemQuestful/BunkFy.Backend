namespace BunkFy.Modules.Guests.Application.Ports;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.ProjectionRebuild;

public interface IGuestsPropertiesProjectionRebuildWriter
    : IProjectionRebuildWriter<PropertyTopologyProjectionExport>;
