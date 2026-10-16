using System.Collections.ObjectModel;
using CoreWCF;
using CoreWCF.Channels;
using CoreWCF.Description;
using CoreWCF.Dispatcher;

namespace Erewhon.Api.Soap;

/// <summary>
/// Adds one <see cref="SoapRequestContractInspector"/> per contract to the message inspectors of every endpoint
/// dispatcher of a service that is not a system endpoint and whose contract name and namespace are those of a
/// non-system endpoint of the service description. Metadata and help-page endpoints get no inspector.
/// </summary>
public sealed class SoapRequestContractBehavior : IServiceBehavior
{
    /// <summary>Does nothing.</summary>
    /// <param name="serviceDescription">The service description. Not used.</param>
    /// <param name="serviceHostBase">The service host. Not used.</param>
    public void Validate(ServiceDescription serviceDescription, ServiceHostBase serviceHostBase)
    {
    }

    /// <summary>Does nothing.</summary>
    /// <param name="serviceDescription">The service description. Not used.</param>
    /// <param name="serviceHostBase">The service host. Not used.</param>
    /// <param name="endpoints">The service endpoints. Not used.</param>
    /// <param name="bindingParameters">The binding parameters. Not used.</param>
    public void AddBindingParameters(
        ServiceDescription serviceDescription,
        ServiceHostBase serviceHostBase,
        Collection<ServiceEndpoint> endpoints,
        BindingParameterCollection bindingParameters)
    {
    }

    /// <summary>
    /// Adds the inspector of each matching contract to the <see cref="DispatchRuntime.MessageInspectors"/> of every
    /// matching endpoint dispatcher of <paramref name="serviceHostBase"/>.
    /// </summary>
    /// <param name="serviceDescription">The description whose non-system endpoints name the checked contracts.</param>
    /// <param name="serviceHostBase">The host whose endpoint dispatchers receive the inspectors.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="serviceDescription"/> or <paramref name="serviceHostBase"/> is <see langword="null"/>.
    /// </exception>
    public void ApplyDispatchBehavior(ServiceDescription serviceDescription, ServiceHostBase serviceHostBase)
    {
        ArgumentNullException.ThrowIfNull(serviceDescription);
        ArgumentNullException.ThrowIfNull(serviceHostBase);

        ContractDescription[] contracts = serviceDescription.Endpoints
            .Where(endpoint => !endpoint.IsSystemEndpoint)
            .Select(endpoint => endpoint.Contract)
            .Distinct()
            .ToArray();
        var inspectors = new Dictionary<ContractDescription, SoapRequestContractInspector>();

        foreach (ChannelDispatcher channelDispatcher in serviceHostBase.ChannelDispatchers.OfType<ChannelDispatcher>())
        {
            foreach (EndpointDispatcher endpointDispatcher in channelDispatcher.Endpoints)
            {
                if (endpointDispatcher.IsSystemEndpoint)
                {
                    continue;
                }

                ContractDescription? contract = contracts.FirstOrDefault(candidate =>
                    candidate.Name == endpointDispatcher.ContractName
                    && candidate.Namespace == endpointDispatcher.ContractNamespace);
                if (contract is null)
                {
                    continue;
                }

                if (!inspectors.TryGetValue(contract, out SoapRequestContractInspector? inspector))
                {
                    inspector = new SoapRequestContractInspector(contract);
                    inspectors.Add(contract, inspector);
                }

                endpointDispatcher.DispatchRuntime.MessageInspectors.Add(inspector);
            }
        }
    }
}
