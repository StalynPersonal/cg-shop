using CgShop.Domain.Common;
using CgShop.Infrastructure.Identity;
using CgShop.Web.Tenancy;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;

namespace CgShop.Web.Components.Pages.Admin;

/// <summary>Base de las páginas del panel del tenant: expone el actor autenticado y servicios de UI.</summary>
public abstract class AdminPageBase : ComponentBase
{
    [CascadingParameter] protected HostContext Host { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState> AuthState { get; set; } = default!;
    [Inject] protected ISnackbar Snackbar { get; set; } = default!;
    [Inject] protected IDialogService Dialogs { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    protected ActorInfo Actor { get; private set; } = ActorInfo.System;
    protected string Currency => Host.Currency;

    protected override async Task OnInitializedAsync()
    {
        Actor = (await AuthState).User.ToActor();
        await OnAdminInitializedAsync();
    }

    protected virtual Task OnAdminInitializedAsync() => Task.CompletedTask;
}
