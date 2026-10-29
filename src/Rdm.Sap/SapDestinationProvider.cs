using Microsoft.Extensions.Options;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Dựng RfcDestination từ <see cref="SapOptions"/> (không dùng file .config của NCo).
/// NCo tự cache destination + connection pool theo bộ tham số.
/// </summary>
public sealed class SapDestinationProvider
{
    private readonly Lazy<RfcConfigParameters> _parameters;

    public SapDestinationProvider(IOptions<SapOptions> options)
    {
        _parameters = new Lazy<RfcConfigParameters>(() => Build(options.Value));
    }

    public RfcDestination Get() => RfcDestinationManager.GetDestination(_parameters.Value);

    private static RfcConfigParameters Build(SapOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Client) || string.IsNullOrWhiteSpace(o.User))
            throw new InvalidOperationException("Thiếu cấu hình Sap:Client / Sap:User.");

        var p = new RfcConfigParameters
        {
            { RfcConfigParameters.Name, o.DestinationName },
            { RfcConfigParameters.Client, o.Client },
            { RfcConfigParameters.User, o.User },
            { RfcConfigParameters.Password, o.Password },
            { RfcConfigParameters.Language, o.Language },
            { RfcConfigParameters.PoolSize, o.PoolSize.ToString() },
            { RfcConfigParameters.MaxPoolWaitTime, o.MaxPoolWaitTimeMs.ToString() },
            { RfcConfigParameters.PoolIdleTimeout, o.IdleTimeoutSeconds.ToString() }
        };

        if (!string.IsNullOrWhiteSpace(o.MessageServerHost))
        {
            p.Add(RfcConfigParameters.MessageServerHost, o.MessageServerHost);
            if (!string.IsNullOrWhiteSpace(o.LogonGroup)) p.Add(RfcConfigParameters.LogonGroup, o.LogonGroup);
            if (!string.IsNullOrWhiteSpace(o.SystemId)) p.Add(RfcConfigParameters.SystemID, o.SystemId);
        }
        else
        {
            p.Add(RfcConfigParameters.AppServerHost, o.AppServerHost);
            p.Add(RfcConfigParameters.SystemNumber, o.SystemNumber);
            if (!string.IsNullOrWhiteSpace(o.SystemId)) p.Add(RfcConfigParameters.SystemID, o.SystemId);
        }

        if (!string.IsNullOrWhiteSpace(o.SapRouter)) p.Add(RfcConfigParameters.SAPRouter, o.SapRouter);
        return p;
    }
}
