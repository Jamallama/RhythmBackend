@description('Globally unique dedicated storage account name, 3–24 lowercase letters/digits.')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('An Azure region permitted by your subscription.')
param location string = resourceGroup().location

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: true
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: false }
    containerDeleteRetentionPolicy: { enabled: false }
    isVersioningEnabled: false
    restorePolicy: { enabled: false }
  }
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'rhythm-sessions'
  properties: { publicAccess: 'None' }
}

// No connection string/account key output: put it in App Service secrets directly.
output blobEndpoint string = account.properties.primaryEndpoints.blob
output containerName string = container.name
