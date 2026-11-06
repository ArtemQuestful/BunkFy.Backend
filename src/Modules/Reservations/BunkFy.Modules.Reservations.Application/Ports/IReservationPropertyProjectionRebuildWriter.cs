namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.ProjectionRebuild;

public interface IReservationPropertyProjectionRebuildWriter
    : IProjectionRebuildWriter<PropertyTopologyProjectionExport>;
