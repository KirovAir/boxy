namespace Boxy.Web;

/// <summary>The token API described twice: a markdown guide an agent can read and act on, and an OpenAPI
/// document for tools that want one. Both carry this instance's base URL so examples work as pasted.
/// Keep them in step with <see cref="Controllers.ApiV1Controller"/>.</summary>
public static class ApiDocs
{
    public static string Guide(string baseUrl)
    {
        return $$"""
            # Boxy API

            Upload files to Boxy and get a share link back.

            Base URL: {{baseUrl}}

            ## Authentication

            Every call under /api/v1 needs an API key:

                Authorization: Bearer boxy_...

            A key is created on the account page ({{baseUrl}}/account). It acts as its owner: it can upload
            shares and read that account's own shares, nothing else.

            ## Upload a file

                curl -H "Authorization: Bearer $BOXY_API_KEY" -F "file=@/path/to/clip.mp4" {{baseUrl}}/api/v1/files

            Optional form fields:

            - `title`: shown on the share page. Defaults to the file name.
            - `conversion`: videos only. `convert` (the server default unless changed) plays in every browser,
              making an H.264 copy when the video needs one. `repackage` never re-encodes, so it's quick, but
              H.265 won't play in every browser.

            The answer is 201 with the share:

                {"id": "k3x9q2mzpa", "title": "clip", "fileName": "clip.mp4", "kind": "video", "sizeBytes": 1048576,
                 "status": "uploaded", "url": "{{baseUrl}}/s/k3x9q2mzpa", "expiresAt": null, "createdAt": "...", "error": null}

            Uploading the same file again returns the share that already exists.

            ## Wait until the link works

            The share link only opens once `status` is `ready`. Other files are ready within seconds; a video can
            take minutes to convert. Poll every few seconds:

                curl -H "Authorization: Bearer $BOXY_API_KEY" {{baseUrl}}/api/v1/files/k3x9q2mzpa

            `status` is one of `uploaded`, `processing`, `ready`, `failed` (then `error` says why).

            ## Other calls

            - `GET /api/v1/files?page=1`: your shares, newest first, 50 per page, with `total`.
            - `GET /api/v1/files/{id}`: one share.
            - `GET /api/v1/me`: your name, the upload size limit and your storage use. 0 means unlimited.

            ## Errors

            Errors are JSON with an `error` message: 400 bad request, 401 missing or wrong key, 404 unknown id,
            413 over the upload limit, 507 storage or quota full.

            OpenAPI document: {{baseUrl}}/api/openapi.json
            """;
    }

    public static string OpenApi(string baseUrl)
    {
        return $$"""
            {
              "openapi": "3.1.0",
              "info": {
                "title": "Boxy API",
                "version": "1",
                "description": "Upload files to Boxy as shares and poll them until their link works. Guide: {{baseUrl}}/api"
              },
              "servers": [{ "url": "{{baseUrl}}" }],
              "security": [{ "apiKey": [] }],
              "paths": {
                "/api/v1/files": {
                  "post": {
                    "summary": "Upload a file as a new share",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "multipart/form-data": {
                          "schema": {
                            "type": "object",
                            "required": ["file"],
                            "properties": {
                              "file": { "type": "string", "format": "binary" },
                              "title": { "type": "string", "description": "Defaults to the file name." },
                              "conversion": {
                                "type": "string",
                                "enum": ["convert", "repackage"],
                                "description": "Videos only. convert: plays in every browser, an H.264 copy is made when needed. repackage: never re-encoded."
                              }
                            }
                          }
                        }
                      }
                    },
                    "responses": {
                      "201": { "description": "The share. Same file again returns the existing one.", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/File" } } } },
                      "400": { "$ref": "#/components/responses/Error" },
                      "401": { "$ref": "#/components/responses/Error" },
                      "413": { "$ref": "#/components/responses/Error" },
                      "507": { "$ref": "#/components/responses/Error" }
                    }
                  },
                  "get": {
                    "summary": "List your shares, newest first",
                    "parameters": [{ "name": "page", "in": "query", "schema": { "type": "integer", "minimum": 1, "default": 1 } }],
                    "responses": {
                      "200": {
                        "description": "One page of 50.",
                        "content": {
                          "application/json": {
                            "schema": {
                              "type": "object",
                              "properties": {
                                "page": { "type": "integer" },
                                "pageSize": { "type": "integer" },
                                "total": { "type": "integer" },
                                "files": { "type": "array", "items": { "$ref": "#/components/schemas/File" } }
                              }
                            }
                          }
                        }
                      },
                      "401": { "$ref": "#/components/responses/Error" }
                    }
                  }
                },
                "/api/v1/files/{id}": {
                  "get": {
                    "summary": "One share; poll until status is ready",
                    "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }],
                    "responses": {
                      "200": { "description": "The share.", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/File" } } } },
                      "401": { "$ref": "#/components/responses/Error" },
                      "404": { "$ref": "#/components/responses/Error" }
                    }
                  }
                },
                "/api/v1/me": {
                  "get": {
                    "summary": "The key's account, upload limit and storage use",
                    "responses": {
                      "200": {
                        "description": "0 means unlimited.",
                        "content": {
                          "application/json": {
                            "schema": {
                              "type": "object",
                              "properties": {
                                "name": { "type": "string" },
                                "username": { "type": ["string", "null"] },
                                "maxUploadBytes": { "type": "integer" },
                                "quotaBytes": { "type": "integer" },
                                "usedBytes": { "type": "integer" }
                              }
                            }
                          }
                        }
                      },
                      "401": { "$ref": "#/components/responses/Error" }
                    }
                  }
                }
              },
              "components": {
                "securitySchemes": {
                  "apiKey": { "type": "http", "scheme": "bearer", "description": "boxy_... key from {{baseUrl}}/account" }
                },
                "schemas": {
                  "File": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" },
                      "title": { "type": "string" },
                      "fileName": { "type": "string" },
                      "kind": { "type": "string", "enum": ["video", "image", "audio", "pdf", "file"] },
                      "sizeBytes": { "type": "integer" },
                      "status": { "type": "string", "enum": ["uploaded", "processing", "ready", "failed"] },
                      "url": { "type": "string", "description": "The share link. Opens once status is ready." },
                      "expiresAt": { "type": ["string", "null"], "format": "date-time" },
                      "createdAt": { "type": "string", "format": "date-time" },
                      "error": { "type": ["string", "null"] }
                    }
                  }
                },
                "responses": {
                  "Error": {
                    "description": "Something went wrong; error says what.",
                    "content": { "application/json": { "schema": { "type": "object", "properties": { "error": { "type": "string" } } } } }
                  }
                }
              }
            }
            """;
    }
}
