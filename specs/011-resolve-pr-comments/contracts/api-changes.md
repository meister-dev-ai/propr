# OpenAPI / Contracts: Resolve PR Comments

The `openapi.json` will be updated to reflect changes to the Client endpoints.

## Modified Endpoints

### `GET /clients/{clientId}` & `GET /clients`
The response schema `ClientDto` will include the new `commentResolutionBehavior` field.

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "displayName": "Example Client",
  "isActive": true,
  "createdAt": "2023-10-01T12:00:00Z",
  "hasAdoCredentials": true,
  "adoTenantId": "tenant-id",
  "adoClientId": "client-id",
  "reviewerId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "commentResolutionBehavior": "Silent" 
}
```
*(Enum values: `Disabled`, `Silent`, `WithReply`)*

### `POST /clients` & `PUT /clients/{clientId}`
The request schemas will accept an optional `commentResolutionBehavior` field.

```json
{
  "displayName": "Example Client",
  "adoTenantId": "tenant-id",
  "adoClientId": "client-id",
  "adoClientSecret": "secret",
  "commentResolutionBehavior": "Silent"
}
```