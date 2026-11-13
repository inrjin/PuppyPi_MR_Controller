using System;
using UnityEngine;

namespace PuppyPi
{
    // Normalized axes are (forward, turn). Native units remain available for the
    // research log; transport and the v2 session never depend on those units.
    public interface IRobotDriveInput
    {
        Vector2 Axes { get; }
        Vector2 RawAxes { get; }
        Vector2 RequestedAxes { get; }
        bool Engaged { get; }
        bool ControlActive { get; }
        bool InputValid { get; }
        bool Focused { get; }
        bool Paused { get; }
        bool IsSimulation { get; }
        string InterfaceId { get; }
        string RawUnits { get; }
        string InputModality { get; }
        event Action NeutralRequested;
        void Invalidate();
    }
}
