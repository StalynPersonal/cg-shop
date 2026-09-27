using Microsoft.Extensions.Options;

namespace CgShop.Application.Platform;

/// <summary>
/// <see cref="IOptions{TOptions}"/> que obtiene el valor vigente en cada acceso a <see cref="Value"/>
/// (la configuración editable por el Super Admin se aplica sin reiniciar).
/// </summary>
public sealed class LiveOptions<TOptions>(Func<TOptions> factory) : IOptions<TOptions> where TOptions : class
{
    public TOptions Value => factory();
}
