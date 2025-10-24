using System;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IInputHookService : IDisposable
{
    event EventHandler<Profile?>? ActiveProfileChanged;

    void Start();

    void Stop();

    void ActivateProfile(Profile profile);

    void DeactivateProfile();

    void SetWindowsProfile(Profile profile);
}
