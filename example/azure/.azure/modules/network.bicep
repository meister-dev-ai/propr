param location string
param vnetName string

resource vnet 'Microsoft.Network/virtualNetworks@2024-07-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.0.0.0/16'] }
    subnets: [
      {
        name: 'infrastructure-subnet'
        properties: {
          addressPrefix: '10.0.0.0/23'
          delegations: [
            {
              name: 'containerApps'
              properties: { serviceName: 'Microsoft.App/environments' }
            }
          ]
          serviceEndpoints: [
            { service: 'Microsoft.KeyVault' }
          ]
          privateEndpointNetworkPolicies: 'Disabled'
          privateLinkServiceNetworkPolicies: 'Enabled'
        }
      }
    ]
    enableDdosProtection: false
  }
}

output subnetId string = vnet.properties.subnets[0].id
