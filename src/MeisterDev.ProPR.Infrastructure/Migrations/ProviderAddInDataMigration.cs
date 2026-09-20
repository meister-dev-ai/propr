// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Moves the rows an installation already holds onto the vocabulary the provider families declare as
    ///     add-ins.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ProviderAddInArchitecture" /> holds the schema half and calls into this one. The two
    ///         are separate files because this half is long and is read for a different reason, and because
    ///         every statement in it is the one that shipped before, unchanged.
    ///     </para>
    ///     <para>
    ///         Order is load-bearing, which is why the schema migration calls three entry points rather than
    ///         one. The logical-model modes change type and value in a single statement that reads the column
    ///         while it is still an integer, so that runs first. A family's vocabulary is rewritten while its
    ///         rows still carry the identity they were written under, then the identity is flipped. Qualifying
    ///         the logical-model modes runs last of all, because it reads each connection's provider key and
    ///         needs the declared one.
    ///     </para>
    /// </remarks>
    internal static class ProviderAddInDataMigration
    {
        private const string Reserved = "'Auto', 'Embeddings'";

        /// <summary>Turns the logical-model protocol modes from numbers into names, and widens the column.</summary>
        /// <param name="migrationBuilder">The migration being applied.</param>
        public static void NameLogicalModelProtocolModes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(NameConversion("ai_logical_models"));
            migrationBuilder.Sql(NameConversion("ai_logical_model_overrides"));
        }

        /// <summary>Puts those modes back to the numbers they were stored as.</summary>
        /// <param name="migrationBuilder">The migration being reverted.</param>
        public static void NumberLogicalModelProtocolModes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RefuseNamesWithNoInteger);
            migrationBuilder.Sql(IntegerConversion("ai_logical_models"));
            migrationBuilder.Sql(IntegerConversion("ai_logical_model_overrides"));
        }

        /// <summary>Rewrites every family's rows onto its declared vocabulary.</summary>
        /// <param name="migrationBuilder">The migration being applied.</param>
        public static void Forward(MigrationBuilder migrationBuilder)
        {
            GoogleVertex.Forward(migrationBuilder);
            OpenAiCompatible.Forward(migrationBuilder);
            LiteLlm.Forward(migrationBuilder);
            AwsBedrock.Forward(migrationBuilder);
            AzureOpenAi.Forward(migrationBuilder);
            OpenAi.Forward(migrationBuilder);
            Anthropic.Forward(migrationBuilder);

            migrationBuilder.Sql(Qualify("ai_logical_models"));
            migrationBuilder.Sql(Qualify("ai_logical_model_overrides"));
        }

        /// <summary>Puts every family's rows back on the vocabulary they arrived with.</summary>
        /// <param name="migrationBuilder">The migration being reverted.</param>
        public static void Back(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Unqualify("ai_logical_model_overrides"));
            migrationBuilder.Sql(Unqualify("ai_logical_models"));

            Anthropic.Back(migrationBuilder);
            OpenAi.Back(migrationBuilder);
            AzureOpenAi.Back(migrationBuilder);
            AwsBedrock.Back(migrationBuilder);
            LiteLlm.Back(migrationBuilder);
            OpenAiCompatible.Back(migrationBuilder);
            GoogleVertex.Back(migrationBuilder);
        }

        private static string NameConversion(string table)
        {
            return $"""
                    ALTER TABLE {table}
                        ALTER COLUMN protocol_mode TYPE character varying(129)
                        USING CASE protocol_mode
                            WHEN 0 THEN 'Auto'
                            WHEN 1 THEN 'Responses'
                            WHEN 2 THEN 'ChatCompletions'
                            WHEN 3 THEN 'Embeddings'
                            WHEN 4 THEN 'AnthropicMessages'
                            WHEN 5 THEN 'BedrockConverse'
                            WHEN 6 THEN 'GoogleGenerateContent'
                            ELSE 'unknown-' || protocol_mode
                        END;
                    """;
        }

        private static string IntegerConversion(string table)
        {
            return $"""
                    ALTER TABLE {table}
                        ALTER COLUMN protocol_mode TYPE integer
                        USING CASE
                            WHEN protocol_mode = 'Auto' THEN 0
                            WHEN protocol_mode = 'Responses' THEN 1
                            WHEN protocol_mode = 'ChatCompletions' THEN 2
                            WHEN protocol_mode = 'Embeddings' THEN 3
                            WHEN protocol_mode = 'AnthropicMessages' THEN 4
                            WHEN protocol_mode = 'BedrockConverse' THEN 5
                            WHEN protocol_mode = 'GoogleGenerateContent' THEN 6
                            WHEN protocol_mode ~ '^unknown-[0-9]+$' THEN substring(protocol_mode from 9)::integer
                        END;
                    """;
        }

        private const string RefuseNamesWithNoInteger =
            """
            DO $$
            DECLARE
                blocking text;
            BEGIN
                SELECT string_agg(DISTINCT quote_literal(protocol_mode), ', ' ORDER BY quote_literal(protocol_mode))
                  INTO blocking
                  FROM (
                        SELECT protocol_mode FROM ai_logical_models
                        UNION
                        SELECT protocol_mode FROM ai_logical_model_overrides
                       ) AS stored
                 WHERE protocol_mode NOT IN (
                           'Auto', 'Responses', 'ChatCompletions', 'Embeddings',
                           'AnthropicMessages', 'BedrockConverse', 'GoogleGenerateContent')
                   AND protocol_mode !~ '^unknown-[0-9]+$';

                IF blocking IS NOT NULL THEN
                    RAISE EXCEPTION
                        'Cannot restore the numeric protocol mode: % name(s) no integer can express are stored on a logical model or an override.',
                        blocking
                        USING HINT = 'Rewrite those rows to a name the numeric vocabulary carries, then revert again.';
                END IF;
            END
            $$;
            """;

        private static string Qualify(string table)
        {
            return $"""
                    UPDATE {table} AS target
                       SET protocol_mode = profile.provider_kind || ':' || target.protocol_mode
                      FROM ai_connection_profiles AS profile
                     WHERE target.connection_id = profile.id
                       AND target.protocol_mode IS NOT NULL
                       AND target.protocol_mode NOT IN ({Reserved})
                       AND position(':' in target.protocol_mode) = 0;
                    """;
        }

        // Only a prefix this migration could have written is removed, so a mode an operator qualified by hand
        // for another family survives going back.
        private static string Unqualify(string table)
        {
            return $"""
                    UPDATE {table} AS target
                       SET protocol_mode = substring(target.protocol_mode from length(profile.provider_kind) + 2)
                      FROM ai_connection_profiles AS profile
                     WHERE target.connection_id = profile.id
                       AND target.protocol_mode IS NOT NULL
                       AND target.protocol_mode NOT IN ({Reserved})
                       AND target.protocol_mode LIKE profile.provider_kind || ':%';
                    """;
        }

        /// <summary>Moves the GoogleVertex family's rows onto the identity and modes it declares as an add-in.</summary>
        private static class GoogleVertex
        {
            private const string LegacyIdentity = "GoogleVertex";
            private const string DeclaredIdentity = "meisterdev/googleVertex";

            private const string DeclaredQualifier = DeclaredIdentity + ":";
            private const string LegacyQualifier = "";

            public static void Forward(MigrationBuilder migrationBuilder)
            {
                // The vocabulary first, while the rows that carry it can still be found by the identity they hold.
                Rewrite(migrationBuilder, LegacyIdentity, LegacyQualifier, DeclaredQualifier);
                migrationBuilder.Sql(Identity(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(TenantAllowList(LegacyIdentity, DeclaredIdentity));
            }

            public static void Back(MigrationBuilder migrationBuilder)
            {
                Rewrite(migrationBuilder, DeclaredIdentity, DeclaredQualifier, LegacyQualifier);
                migrationBuilder.Sql(Identity(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(TenantAllowList(DeclaredIdentity, LegacyIdentity));
            }

            private static void Rewrite(
                MigrationBuilder migrationBuilder,
                string storedIdentity,
                string fromQualifier,
                string toQualifier)
            {
                migrationBuilder.Sql(CredentialShapes(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(BoundWireShape(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(ListedWireShapes(storedIdentity, fromQualifier, toQualifier));
            }

            // The two authentication modes the family declares. A connection holding anything else is left alone: the
            // value names no shape this family serves, and the read path reports it rather than guessing.
            private static string CredentialShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET auth_mode = CASE auth_mode
                                   WHEN '{fromQualifier}ApiKey' THEN '{toQualifier}ApiKey'
                                   WHEN '{fromQualifier}GcpAdc' THEN '{toQualifier}GcpAdc'
                                   ELSE auth_mode
                               END
                         WHERE provider_kind = '{storedIdentity}';
                        """;
            }

            private static string BoundWireShape(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_purpose_bindings
                           SET protocol_mode = '{toQualifier}GoogleGenerateContent'
                         WHERE protocol_mode = '{fromQualifier}GoogleGenerateContent'
                           AND connection_profile_id IN (
                               SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}');
                        """;
            }

            // The listed shapes are a JSON array, rewritten element by element so the order an operator sees is the
            // order that comes back.
            private static string ListedWireShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_configured_models
                           SET supported_protocol_modes = (
                                   SELECT jsonb_agg(
                                              CASE listed.mode
                                                  WHEN '{fromQualifier}GoogleGenerateContent'
                                                      THEN to_jsonb('{toQualifier}GoogleGenerateContent'::text)
                                                  ELSE to_jsonb(listed.mode)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(supported_protocol_modes)
                                          WITH ORDINALITY AS listed(mode, ordinal))
                         WHERE connection_profile_id IN (
                                   SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}')
                           AND supported_protocol_modes @> to_jsonb('{fromQualifier}GoogleGenerateContent'::text);
                        """;
            }

            private static string Identity(string from, string to)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET provider_kind = '{to}'
                         WHERE provider_kind = '{from}';
                        """;
            }

            private static string TenantAllowList(string from, string to)
            {
                return $"""
                        UPDATE tenants
                           SET allowed_ai_provider_kinds = (
                                   SELECT jsonb_agg(
                                              CASE listed.entry
                                                  WHEN '{from}' THEN to_jsonb('{to}'::text)
                                                  ELSE to_jsonb(listed.entry)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(allowed_ai_provider_kinds)
                                          WITH ORDINALITY AS listed(entry, ordinal))
                         WHERE allowed_ai_provider_kinds @> to_jsonb('{from}'::text);
                        """;
            }

            // Summed into the target row and deleted, rather than updated in place: the identity is part of the
            // unique index, so an update onto an identity a row already holds for the same client, model, logical
            // model and day violates it and takes the whole migration down with it.
            private static string FoldedUsageSamples(string from, string to)
            {
                return $"""
                        WITH moved AS (
                            DELETE FROM client_token_usage_samples
                             WHERE provider_kind = '{from}'
                            RETURNING client_id, model_id, logical_model_name, date, input_tokens, output_tokens,
                                      cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd
                        ), totals AS (
                            SELECT client_id,
                                   model_id,
                                   logical_model_name,
                                   date,
                                   SUM(input_tokens) AS input_tokens,
                                   SUM(output_tokens) AS output_tokens,
                                   SUM(cached_input_tokens) AS cached_input_tokens,
                                   SUM(cache_write_tokens) AS cache_write_tokens,
                                   SUM(reasoning_tokens) AS reasoning_tokens,
                                   SUM(estimated_cost_usd) AS estimated_cost_usd
                              FROM moved
                             GROUP BY client_id, model_id, logical_model_name, date
                        )
                        INSERT INTO client_token_usage_samples
                            (id, client_id, model_id, logical_model_name, provider_kind, date, input_tokens,
                             output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                             estimated_cost_usd)
                        SELECT gen_random_uuid(), client_id, model_id, logical_model_name, '{to}', date, input_tokens,
                               output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                               estimated_cost_usd
                          FROM totals
                        ON CONFLICT (client_id, model_id, logical_model_name, provider_kind, date)
                        DO UPDATE SET
                            input_tokens        = client_token_usage_samples.input_tokens        + EXCLUDED.input_tokens,
                            output_tokens       = client_token_usage_samples.output_tokens       + EXCLUDED.output_tokens,
                            cached_input_tokens = client_token_usage_samples.cached_input_tokens + EXCLUDED.cached_input_tokens,
                            cache_write_tokens  = client_token_usage_samples.cache_write_tokens  + EXCLUDED.cache_write_tokens,
                            reasoning_tokens    = client_token_usage_samples.reasoning_tokens    + EXCLUDED.reasoning_tokens,
                            estimated_cost_usd  = CASE
                                                      WHEN client_token_usage_samples.estimated_cost_usd IS NULL
                                                           AND EXCLUDED.estimated_cost_usd IS NULL THEN NULL
                                                      ELSE COALESCE(client_token_usage_samples.estimated_cost_usd, 0)
                                                           + COALESCE(EXCLUDED.estimated_cost_usd, 0)
                                                  END;
                        """;
            }
        }

        /// <summary>Moves the OpenAiCompatible family's rows onto the identity and modes it declares as an add-in.</summary>
        private static class OpenAiCompatible
        {
            private const string LegacyIdentity = "OpenAiCompatible";
            private const string DeclaredIdentity = "meisterdev/openAiCompatible";

            private const string DeclaredQualifier = DeclaredIdentity + ":";
            private const string LegacyQualifier = "";

            public static void Forward(MigrationBuilder migrationBuilder)
            {
                // The vocabulary first, while the rows that carry it can still be found by the identity they hold.
                Rewrite(migrationBuilder, LegacyIdentity, LegacyQualifier, DeclaredQualifier);
                migrationBuilder.Sql(Identity(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(TenantAllowList(LegacyIdentity, DeclaredIdentity));
            }

            public static void Back(MigrationBuilder migrationBuilder)
            {
                Rewrite(migrationBuilder, DeclaredIdentity, DeclaredQualifier, LegacyQualifier);
                migrationBuilder.Sql(Identity(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(TenantAllowList(DeclaredIdentity, LegacyIdentity));
            }

            private static void Rewrite(
                MigrationBuilder migrationBuilder,
                string storedIdentity,
                string fromQualifier,
                string toQualifier)
            {
                migrationBuilder.Sql(CredentialShapes(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(BoundWireShape(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(ListedWireShapes(storedIdentity, fromQualifier, toQualifier));
            }

            // The one authentication mode the family declares. A connection holding anything else is left alone: the
            // value names no shape this family serves, and the read path reports it rather than guessing.
            private static string CredentialShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET auth_mode = '{toQualifier}ApiKey'
                         WHERE provider_kind = '{storedIdentity}'
                           AND auth_mode = '{fromQualifier}ApiKey';
                        """;
            }

            private static string BoundWireShape(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_purpose_bindings
                           SET protocol_mode = '{toQualifier}ChatCompletions'
                         WHERE protocol_mode = '{fromQualifier}ChatCompletions'
                           AND connection_profile_id IN (
                               SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}');
                        """;
            }

            // The listed shapes are a JSON array, rewritten element by element so the order an operator sees is the
            // order that comes back.
            private static string ListedWireShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_configured_models
                           SET supported_protocol_modes = (
                                   SELECT jsonb_agg(
                                              CASE listed.mode
                                                  WHEN '{fromQualifier}ChatCompletions'
                                                      THEN to_jsonb('{toQualifier}ChatCompletions'::text)
                                                  ELSE to_jsonb(listed.mode)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(supported_protocol_modes)
                                          WITH ORDINALITY AS listed(mode, ordinal))
                         WHERE connection_profile_id IN (
                                   SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}')
                           AND supported_protocol_modes @> to_jsonb('{fromQualifier}ChatCompletions'::text);
                        """;
            }

            private static string Identity(string from, string to)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET provider_kind = '{to}'
                         WHERE provider_kind = '{from}';
                        """;
            }

            private static string TenantAllowList(string from, string to)
            {
                return $"""
                        UPDATE tenants
                           SET allowed_ai_provider_kinds = (
                                   SELECT jsonb_agg(
                                              CASE listed.entry
                                                  WHEN '{from}' THEN to_jsonb('{to}'::text)
                                                  ELSE to_jsonb(listed.entry)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(allowed_ai_provider_kinds)
                                          WITH ORDINALITY AS listed(entry, ordinal))
                         WHERE allowed_ai_provider_kinds @> to_jsonb('{from}'::text);
                        """;
            }

            // Summed into the target row and deleted, rather than updated in place: the identity is part of the
            // unique index, so an update onto an identity a row already holds for the same client, model, logical
            // model and day violates it and takes the whole migration down with it.
            private static string FoldedUsageSamples(string from, string to)
            {
                return $"""
                        WITH moved AS (
                            DELETE FROM client_token_usage_samples
                             WHERE provider_kind = '{from}'
                            RETURNING client_id, model_id, logical_model_name, date, input_tokens, output_tokens,
                                      cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd
                        ), totals AS (
                            SELECT client_id,
                                   model_id,
                                   logical_model_name,
                                   date,
                                   SUM(input_tokens) AS input_tokens,
                                   SUM(output_tokens) AS output_tokens,
                                   SUM(cached_input_tokens) AS cached_input_tokens,
                                   SUM(cache_write_tokens) AS cache_write_tokens,
                                   SUM(reasoning_tokens) AS reasoning_tokens,
                                   SUM(estimated_cost_usd) AS estimated_cost_usd
                              FROM moved
                             GROUP BY client_id, model_id, logical_model_name, date
                        )
                        INSERT INTO client_token_usage_samples
                            (id, client_id, model_id, logical_model_name, provider_kind, date, input_tokens,
                             output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                             estimated_cost_usd)
                        SELECT gen_random_uuid(), client_id, model_id, logical_model_name, '{to}', date, input_tokens,
                               output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                               estimated_cost_usd
                          FROM totals
                        ON CONFLICT (client_id, model_id, logical_model_name, provider_kind, date)
                        DO UPDATE SET
                            input_tokens        = client_token_usage_samples.input_tokens        + EXCLUDED.input_tokens,
                            output_tokens       = client_token_usage_samples.output_tokens       + EXCLUDED.output_tokens,
                            cached_input_tokens = client_token_usage_samples.cached_input_tokens + EXCLUDED.cached_input_tokens,
                            cache_write_tokens  = client_token_usage_samples.cache_write_tokens  + EXCLUDED.cache_write_tokens,
                            reasoning_tokens    = client_token_usage_samples.reasoning_tokens    + EXCLUDED.reasoning_tokens,
                            estimated_cost_usd  = CASE
                                                      WHEN client_token_usage_samples.estimated_cost_usd IS NULL
                                                           AND EXCLUDED.estimated_cost_usd IS NULL THEN NULL
                                                      ELSE COALESCE(client_token_usage_samples.estimated_cost_usd, 0)
                                                           + COALESCE(EXCLUDED.estimated_cost_usd, 0)
                                                  END;
                        """;
            }
        }

        /// <summary>Moves the LiteLlm family's rows onto the identity and modes it declares as an add-in.</summary>
        private static class LiteLlm
        {
            private const string LegacyIdentity = "LiteLlm";
            private const string DeclaredIdentity = "meisterdev/liteLlm";

            private const string DeclaredQualifier = DeclaredIdentity + ":";
            private const string LegacyQualifier = "";

            public static void Forward(MigrationBuilder migrationBuilder)
            {
                // The vocabulary first, while the rows that carry it can still be found by the identity they hold.
                Rewrite(migrationBuilder, LegacyIdentity, LegacyQualifier, DeclaredQualifier);
                migrationBuilder.Sql(Identity(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(TenantAllowList(LegacyIdentity, DeclaredIdentity));
            }

            public static void Back(MigrationBuilder migrationBuilder)
            {
                Rewrite(migrationBuilder, DeclaredIdentity, DeclaredQualifier, LegacyQualifier);
                migrationBuilder.Sql(Identity(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(TenantAllowList(DeclaredIdentity, LegacyIdentity));
            }

            private static void Rewrite(
                MigrationBuilder migrationBuilder,
                string storedIdentity,
                string fromQualifier,
                string toQualifier)
            {
                migrationBuilder.Sql(CredentialShapes(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(BoundWireShape(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(ListedWireShapes(storedIdentity, fromQualifier, toQualifier));
            }

            // The one authentication mode the family declares. A connection holding anything else is left alone: the
            // value names no shape this family serves, and the read path reports it rather than guessing.
            private static string CredentialShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET auth_mode = '{toQualifier}ApiKey'
                         WHERE provider_kind = '{storedIdentity}'
                           AND auth_mode = '{fromQualifier}ApiKey';
                        """;
            }

            // The two protocol modes the family owns. A binding holding anything else is left alone: the value names no
            // shape this family serves, and the read path reports it rather than guessing.
            private static string BoundWireShape(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_purpose_bindings
                           SET protocol_mode = CASE protocol_mode
                                   WHEN '{fromQualifier}Responses' THEN '{toQualifier}Responses'
                                   WHEN '{fromQualifier}ChatCompletions' THEN '{toQualifier}ChatCompletions'
                                   ELSE protocol_mode
                               END
                         WHERE protocol_mode IN ('{fromQualifier}Responses', '{fromQualifier}ChatCompletions')
                           AND connection_profile_id IN (
                               SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}');
                        """;
            }

            // The listed shapes are a JSON array, rewritten element by element so the order an operator sees is the
            // order that comes back.
            private static string ListedWireShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_configured_models
                           SET supported_protocol_modes = (
                                   SELECT jsonb_agg(
                                              CASE listed.mode
                                                  WHEN '{fromQualifier}Responses'
                                                      THEN to_jsonb('{toQualifier}Responses'::text)
                                                  WHEN '{fromQualifier}ChatCompletions'
                                                      THEN to_jsonb('{toQualifier}ChatCompletions'::text)
                                                  ELSE to_jsonb(listed.mode)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(supported_protocol_modes)
                                          WITH ORDINALITY AS listed(mode, ordinal))
                         WHERE connection_profile_id IN (
                                   SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}')
                           AND (supported_protocol_modes @> to_jsonb('{fromQualifier}Responses'::text)
                                OR supported_protocol_modes @> to_jsonb('{fromQualifier}ChatCompletions'::text));
                        """;
            }

            private static string Identity(string from, string to)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET provider_kind = '{to}'
                         WHERE provider_kind = '{from}';
                        """;
            }

            private static string TenantAllowList(string from, string to)
            {
                return $"""
                        UPDATE tenants
                           SET allowed_ai_provider_kinds = (
                                   SELECT jsonb_agg(
                                              CASE listed.entry
                                                  WHEN '{from}' THEN to_jsonb('{to}'::text)
                                                  ELSE to_jsonb(listed.entry)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(allowed_ai_provider_kinds)
                                          WITH ORDINALITY AS listed(entry, ordinal))
                         WHERE allowed_ai_provider_kinds @> to_jsonb('{from}'::text);
                        """;
            }

            // Summed into the target row and deleted, rather than updated in place: the identity is part of the
            // unique index, so an update onto an identity a row already holds for the same client, model, logical
            // model and day violates it and takes the whole migration down with it.
            private static string FoldedUsageSamples(string from, string to)
            {
                return $"""
                        WITH moved AS (
                            DELETE FROM client_token_usage_samples
                             WHERE provider_kind = '{from}'
                            RETURNING client_id, model_id, logical_model_name, date, input_tokens, output_tokens,
                                      cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd
                        ), totals AS (
                            SELECT client_id,
                                   model_id,
                                   logical_model_name,
                                   date,
                                   SUM(input_tokens) AS input_tokens,
                                   SUM(output_tokens) AS output_tokens,
                                   SUM(cached_input_tokens) AS cached_input_tokens,
                                   SUM(cache_write_tokens) AS cache_write_tokens,
                                   SUM(reasoning_tokens) AS reasoning_tokens,
                                   SUM(estimated_cost_usd) AS estimated_cost_usd
                              FROM moved
                             GROUP BY client_id, model_id, logical_model_name, date
                        )
                        INSERT INTO client_token_usage_samples
                            (id, client_id, model_id, logical_model_name, provider_kind, date, input_tokens,
                             output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                             estimated_cost_usd)
                        SELECT gen_random_uuid(), client_id, model_id, logical_model_name, '{to}', date, input_tokens,
                               output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                               estimated_cost_usd
                          FROM totals
                        ON CONFLICT (client_id, model_id, logical_model_name, provider_kind, date)
                        DO UPDATE SET
                            input_tokens        = client_token_usage_samples.input_tokens        + EXCLUDED.input_tokens,
                            output_tokens       = client_token_usage_samples.output_tokens       + EXCLUDED.output_tokens,
                            cached_input_tokens = client_token_usage_samples.cached_input_tokens + EXCLUDED.cached_input_tokens,
                            cache_write_tokens  = client_token_usage_samples.cache_write_tokens  + EXCLUDED.cache_write_tokens,
                            reasoning_tokens    = client_token_usage_samples.reasoning_tokens    + EXCLUDED.reasoning_tokens,
                            estimated_cost_usd  = CASE
                                                      WHEN client_token_usage_samples.estimated_cost_usd IS NULL
                                                           AND EXCLUDED.estimated_cost_usd IS NULL THEN NULL
                                                      ELSE COALESCE(client_token_usage_samples.estimated_cost_usd, 0)
                                                           + COALESCE(EXCLUDED.estimated_cost_usd, 0)
                                                  END;
                        """;
            }
        }

        /// <summary>Moves the AwsBedrock family's rows onto the identity and modes it declares as an add-in.</summary>
        private static class AwsBedrock
        {
            private const string LegacyIdentity = "AwsBedrock";
            private const string DeclaredIdentity = "meisterdev/awsBedrock";

            private const string DeclaredQualifier = DeclaredIdentity + ":";
            private const string LegacyQualifier = "";

            public static void Forward(MigrationBuilder migrationBuilder)
            {
                // The vocabulary first, while the rows that carry it can still be found by the identity they hold.
                Rewrite(migrationBuilder, LegacyIdentity, LegacyQualifier, DeclaredQualifier);
                migrationBuilder.Sql(Identity(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(TenantAllowList(LegacyIdentity, DeclaredIdentity));
            }

            public static void Back(MigrationBuilder migrationBuilder)
            {
                Rewrite(migrationBuilder, DeclaredIdentity, DeclaredQualifier, LegacyQualifier);
                migrationBuilder.Sql(Identity(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(TenantAllowList(DeclaredIdentity, LegacyIdentity));
            }

            private static void Rewrite(
                MigrationBuilder migrationBuilder,
                string storedIdentity,
                string fromQualifier,
                string toQualifier)
            {
                migrationBuilder.Sql(CredentialShapes(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(BoundWireShape(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(ListedWireShapes(storedIdentity, fromQualifier, toQualifier));
            }

            // The two authentication modes the family declares. A connection holding anything else is left alone: the
            // value names no shape this family serves, and the read path reports it rather than guessing.
            private static string CredentialShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET auth_mode = CASE auth_mode
                                   WHEN '{fromQualifier}SigV4' THEN '{toQualifier}SigV4'
                                   WHEN '{fromQualifier}ApiKey' THEN '{toQualifier}ApiKey'
                               END
                         WHERE provider_kind = '{storedIdentity}'
                           AND auth_mode IN ('{fromQualifier}SigV4', '{fromQualifier}ApiKey');
                        """;
            }

            private static string BoundWireShape(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_purpose_bindings
                           SET protocol_mode = '{toQualifier}BedrockConverse'
                         WHERE protocol_mode = '{fromQualifier}BedrockConverse'
                           AND connection_profile_id IN (
                               SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}');
                        """;
            }

            // The listed shapes are a JSON array, rewritten element by element so the order an operator sees is the
            // order that comes back.
            private static string ListedWireShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_configured_models
                           SET supported_protocol_modes = (
                                   SELECT jsonb_agg(
                                              CASE listed.mode
                                                  WHEN '{fromQualifier}BedrockConverse'
                                                      THEN to_jsonb('{toQualifier}BedrockConverse'::text)
                                                  ELSE to_jsonb(listed.mode)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(supported_protocol_modes)
                                          WITH ORDINALITY AS listed(mode, ordinal))
                         WHERE connection_profile_id IN (
                                   SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}')
                           AND supported_protocol_modes @> to_jsonb('{fromQualifier}BedrockConverse'::text);
                        """;
            }

            private static string Identity(string from, string to)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET provider_kind = '{to}'
                         WHERE provider_kind = '{from}';
                        """;
            }

            private static string TenantAllowList(string from, string to)
            {
                return $"""
                        UPDATE tenants
                           SET allowed_ai_provider_kinds = (
                                   SELECT jsonb_agg(
                                              CASE listed.entry
                                                  WHEN '{from}' THEN to_jsonb('{to}'::text)
                                                  ELSE to_jsonb(listed.entry)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(allowed_ai_provider_kinds)
                                          WITH ORDINALITY AS listed(entry, ordinal))
                         WHERE allowed_ai_provider_kinds @> to_jsonb('{from}'::text);
                        """;
            }

            // Summed into the target row and deleted, rather than updated in place: the identity is part of the
            // unique index, so an update onto an identity a row already holds for the same client, model, logical
            // model and day violates it and takes the whole migration down with it.
            private static string FoldedUsageSamples(string from, string to)
            {
                return $"""
                        WITH moved AS (
                            DELETE FROM client_token_usage_samples
                             WHERE provider_kind = '{from}'
                            RETURNING client_id, model_id, logical_model_name, date, input_tokens, output_tokens,
                                      cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd
                        ), totals AS (
                            SELECT client_id,
                                   model_id,
                                   logical_model_name,
                                   date,
                                   SUM(input_tokens) AS input_tokens,
                                   SUM(output_tokens) AS output_tokens,
                                   SUM(cached_input_tokens) AS cached_input_tokens,
                                   SUM(cache_write_tokens) AS cache_write_tokens,
                                   SUM(reasoning_tokens) AS reasoning_tokens,
                                   SUM(estimated_cost_usd) AS estimated_cost_usd
                              FROM moved
                             GROUP BY client_id, model_id, logical_model_name, date
                        )
                        INSERT INTO client_token_usage_samples
                            (id, client_id, model_id, logical_model_name, provider_kind, date, input_tokens,
                             output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                             estimated_cost_usd)
                        SELECT gen_random_uuid(), client_id, model_id, logical_model_name, '{to}', date, input_tokens,
                               output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                               estimated_cost_usd
                          FROM totals
                        ON CONFLICT (client_id, model_id, logical_model_name, provider_kind, date)
                        DO UPDATE SET
                            input_tokens        = client_token_usage_samples.input_tokens        + EXCLUDED.input_tokens,
                            output_tokens       = client_token_usage_samples.output_tokens       + EXCLUDED.output_tokens,
                            cached_input_tokens = client_token_usage_samples.cached_input_tokens + EXCLUDED.cached_input_tokens,
                            cache_write_tokens  = client_token_usage_samples.cache_write_tokens  + EXCLUDED.cache_write_tokens,
                            reasoning_tokens    = client_token_usage_samples.reasoning_tokens    + EXCLUDED.reasoning_tokens,
                            estimated_cost_usd  = CASE
                                                      WHEN client_token_usage_samples.estimated_cost_usd IS NULL
                                                           AND EXCLUDED.estimated_cost_usd IS NULL THEN NULL
                                                      ELSE COALESCE(client_token_usage_samples.estimated_cost_usd, 0)
                                                           + COALESCE(EXCLUDED.estimated_cost_usd, 0)
                                                  END;
                        """;
            }
        }

        /// <summary>Moves the AzureOpenAi family's rows onto the identity and modes it declares as an add-in.</summary>
        private static class AzureOpenAi
        {
            private const string LegacyIdentity = "AzureOpenAi";
            private const string DeclaredIdentity = "meisterdev/azureOpenAi";

            private const string DeclaredQualifier = DeclaredIdentity + ":";
            private const string LegacyQualifier = "";

            public static void Forward(MigrationBuilder migrationBuilder)
            {
                // The vocabulary first, while the rows that carry it can still be found by the identity they hold.
                Rewrite(migrationBuilder, LegacyIdentity, LegacyQualifier, DeclaredQualifier);
                migrationBuilder.Sql(Identity(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(TenantAllowList(LegacyIdentity, DeclaredIdentity));
            }

            public static void Back(MigrationBuilder migrationBuilder)
            {
                Rewrite(migrationBuilder, DeclaredIdentity, DeclaredQualifier, LegacyQualifier);
                migrationBuilder.Sql(Identity(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(TenantAllowList(DeclaredIdentity, LegacyIdentity));
            }

            private static void Rewrite(
                MigrationBuilder migrationBuilder,
                string storedIdentity,
                string fromQualifier,
                string toQualifier)
            {
                migrationBuilder.Sql(CredentialShapes(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(BoundWireShape(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(ListedWireShapes(storedIdentity, fromQualifier, toQualifier));
            }

            // The two authentication modes the family declares. A connection holding anything else is left alone: the
            // value names no shape this family serves, and the read path reports it rather than guessing.
            private static string CredentialShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET auth_mode = CASE auth_mode
                                   WHEN '{fromQualifier}ApiKey' THEN '{toQualifier}ApiKey'
                                   WHEN '{fromQualifier}AzureIdentity' THEN '{toQualifier}AzureIdentity'
                               END
                         WHERE provider_kind = '{storedIdentity}'
                           AND auth_mode IN ('{fromQualifier}ApiKey', '{fromQualifier}AzureIdentity');
                        """;
            }

            private static string BoundWireShape(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_purpose_bindings
                           SET protocol_mode = CASE protocol_mode
                                   WHEN '{fromQualifier}Responses' THEN '{toQualifier}Responses'
                                   WHEN '{fromQualifier}ChatCompletions' THEN '{toQualifier}ChatCompletions'
                               END
                         WHERE protocol_mode IN ('{fromQualifier}Responses', '{fromQualifier}ChatCompletions')
                           AND connection_profile_id IN (
                               SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}');
                        """;
            }

            // The listed shapes are a JSON array, rewritten element by element so the order an operator sees is the
            // order that comes back.
            private static string ListedWireShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_configured_models
                           SET supported_protocol_modes = (
                                   SELECT jsonb_agg(
                                              CASE listed.mode
                                                  WHEN '{fromQualifier}Responses'
                                                      THEN to_jsonb('{toQualifier}Responses'::text)
                                                  WHEN '{fromQualifier}ChatCompletions'
                                                      THEN to_jsonb('{toQualifier}ChatCompletions'::text)
                                                  ELSE to_jsonb(listed.mode)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(supported_protocol_modes)
                                          WITH ORDINALITY AS listed(mode, ordinal))
                         WHERE connection_profile_id IN (
                                   SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}')
                           AND (supported_protocol_modes @> to_jsonb('{fromQualifier}Responses'::text)
                                OR supported_protocol_modes @> to_jsonb('{fromQualifier}ChatCompletions'::text));
                        """;
            }

            private static string Identity(string from, string to)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET provider_kind = '{to}'
                         WHERE provider_kind = '{from}';
                        """;
            }

            private static string TenantAllowList(string from, string to)
            {
                return $"""
                        UPDATE tenants
                           SET allowed_ai_provider_kinds = (
                                   SELECT jsonb_agg(
                                              CASE listed.entry
                                                  WHEN '{from}' THEN to_jsonb('{to}'::text)
                                                  ELSE to_jsonb(listed.entry)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(allowed_ai_provider_kinds)
                                          WITH ORDINALITY AS listed(entry, ordinal))
                         WHERE allowed_ai_provider_kinds @> to_jsonb('{from}'::text);
                        """;
            }

            // Summed into the target row and deleted, rather than updated in place: the identity is part of the
            // unique index, so an update onto an identity a row already holds for the same client, model, logical
            // model and day violates it and takes the whole migration down with it.
            private static string FoldedUsageSamples(string from, string to)
            {
                return $"""
                        WITH moved AS (
                            DELETE FROM client_token_usage_samples
                             WHERE provider_kind = '{from}'
                            RETURNING client_id, model_id, logical_model_name, date, input_tokens, output_tokens,
                                      cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd
                        ), totals AS (
                            SELECT client_id,
                                   model_id,
                                   logical_model_name,
                                   date,
                                   SUM(input_tokens) AS input_tokens,
                                   SUM(output_tokens) AS output_tokens,
                                   SUM(cached_input_tokens) AS cached_input_tokens,
                                   SUM(cache_write_tokens) AS cache_write_tokens,
                                   SUM(reasoning_tokens) AS reasoning_tokens,
                                   SUM(estimated_cost_usd) AS estimated_cost_usd
                              FROM moved
                             GROUP BY client_id, model_id, logical_model_name, date
                        )
                        INSERT INTO client_token_usage_samples
                            (id, client_id, model_id, logical_model_name, provider_kind, date, input_tokens,
                             output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                             estimated_cost_usd)
                        SELECT gen_random_uuid(), client_id, model_id, logical_model_name, '{to}', date, input_tokens,
                               output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                               estimated_cost_usd
                          FROM totals
                        ON CONFLICT (client_id, model_id, logical_model_name, provider_kind, date)
                        DO UPDATE SET
                            input_tokens        = client_token_usage_samples.input_tokens        + EXCLUDED.input_tokens,
                            output_tokens       = client_token_usage_samples.output_tokens       + EXCLUDED.output_tokens,
                            cached_input_tokens = client_token_usage_samples.cached_input_tokens + EXCLUDED.cached_input_tokens,
                            cache_write_tokens  = client_token_usage_samples.cache_write_tokens  + EXCLUDED.cache_write_tokens,
                            reasoning_tokens    = client_token_usage_samples.reasoning_tokens    + EXCLUDED.reasoning_tokens,
                            estimated_cost_usd  = CASE
                                                      WHEN client_token_usage_samples.estimated_cost_usd IS NULL
                                                           AND EXCLUDED.estimated_cost_usd IS NULL THEN NULL
                                                      ELSE COALESCE(client_token_usage_samples.estimated_cost_usd, 0)
                                                           + COALESCE(EXCLUDED.estimated_cost_usd, 0)
                                                  END;
                        """;
            }
        }

        /// <summary>Moves the OpenAi family's rows onto the identity and modes it declares as an add-in.</summary>
        private static class OpenAi
        {
            private const string LegacyIdentity = "OpenAi";
            private const string DeclaredIdentity = "meisterdev/openAi";

            private const string DeclaredQualifier = DeclaredIdentity + ":";
            private const string LegacyQualifier = "";

            public static void Forward(MigrationBuilder migrationBuilder)
            {
                // The vocabulary first, while the rows that carry it can still be found by the identity they hold.
                Rewrite(migrationBuilder, LegacyIdentity, LegacyQualifier, DeclaredQualifier);
                migrationBuilder.Sql(Identity(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(TenantAllowList(LegacyIdentity, DeclaredIdentity));
            }

            public static void Back(MigrationBuilder migrationBuilder)
            {
                Rewrite(migrationBuilder, DeclaredIdentity, DeclaredQualifier, LegacyQualifier);
                migrationBuilder.Sql(Identity(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(TenantAllowList(DeclaredIdentity, LegacyIdentity));
            }

            private static void Rewrite(
                MigrationBuilder migrationBuilder,
                string storedIdentity,
                string fromQualifier,
                string toQualifier)
            {
                migrationBuilder.Sql(CredentialShapes(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(BoundWireShape(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(ListedWireShapes(storedIdentity, fromQualifier, toQualifier));
            }

            // The one authentication mode the family declares. A connection holding anything else is left alone: the
            // value names no shape this family serves, and the read path reports it rather than guessing.
            private static string CredentialShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET auth_mode = '{toQualifier}ApiKey'
                         WHERE provider_kind = '{storedIdentity}'
                           AND auth_mode = '{fromQualifier}ApiKey';
                        """;
            }

            // The two protocol modes the family owns. A binding holding anything else is left alone: the value names no
            // shape this family serves, and the read path reports it rather than guessing.
            private static string BoundWireShape(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_purpose_bindings
                           SET protocol_mode = CASE protocol_mode
                                   WHEN '{fromQualifier}Responses' THEN '{toQualifier}Responses'
                                   WHEN '{fromQualifier}ChatCompletions' THEN '{toQualifier}ChatCompletions'
                                   ELSE protocol_mode
                               END
                         WHERE protocol_mode IN ('{fromQualifier}Responses', '{fromQualifier}ChatCompletions')
                           AND connection_profile_id IN (
                               SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}');
                        """;
            }

            // The listed shapes are a JSON array, rewritten element by element so the order an operator sees is the
            // order that comes back.
            private static string ListedWireShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_configured_models
                           SET supported_protocol_modes = (
                                   SELECT jsonb_agg(
                                              CASE listed.mode
                                                  WHEN '{fromQualifier}Responses'
                                                      THEN to_jsonb('{toQualifier}Responses'::text)
                                                  WHEN '{fromQualifier}ChatCompletions'
                                                      THEN to_jsonb('{toQualifier}ChatCompletions'::text)
                                                  ELSE to_jsonb(listed.mode)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(supported_protocol_modes)
                                          WITH ORDINALITY AS listed(mode, ordinal))
                         WHERE connection_profile_id IN (
                                   SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}')
                           AND (supported_protocol_modes @> to_jsonb('{fromQualifier}Responses'::text)
                                OR supported_protocol_modes @> to_jsonb('{fromQualifier}ChatCompletions'::text));
                        """;
            }

            private static string Identity(string from, string to)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET provider_kind = '{to}'
                         WHERE provider_kind = '{from}';
                        """;
            }

            private static string TenantAllowList(string from, string to)
            {
                return $"""
                        UPDATE tenants
                           SET allowed_ai_provider_kinds = (
                                   SELECT jsonb_agg(
                                              CASE listed.entry
                                                  WHEN '{from}' THEN to_jsonb('{to}'::text)
                                                  ELSE to_jsonb(listed.entry)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(allowed_ai_provider_kinds)
                                          WITH ORDINALITY AS listed(entry, ordinal))
                         WHERE allowed_ai_provider_kinds @> to_jsonb('{from}'::text);
                        """;
            }

            // Summed into the target row and deleted, rather than updated in place: the identity is part of the
            // unique index, so an update onto an identity a row already holds for the same client, model, logical
            // model and day violates it and takes the whole migration down with it.
            private static string FoldedUsageSamples(string from, string to)
            {
                return $"""
                        WITH moved AS (
                            DELETE FROM client_token_usage_samples
                             WHERE provider_kind = '{from}'
                            RETURNING client_id, model_id, logical_model_name, date, input_tokens, output_tokens,
                                      cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd
                        ), totals AS (
                            SELECT client_id,
                                   model_id,
                                   logical_model_name,
                                   date,
                                   SUM(input_tokens) AS input_tokens,
                                   SUM(output_tokens) AS output_tokens,
                                   SUM(cached_input_tokens) AS cached_input_tokens,
                                   SUM(cache_write_tokens) AS cache_write_tokens,
                                   SUM(reasoning_tokens) AS reasoning_tokens,
                                   SUM(estimated_cost_usd) AS estimated_cost_usd
                              FROM moved
                             GROUP BY client_id, model_id, logical_model_name, date
                        )
                        INSERT INTO client_token_usage_samples
                            (id, client_id, model_id, logical_model_name, provider_kind, date, input_tokens,
                             output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                             estimated_cost_usd)
                        SELECT gen_random_uuid(), client_id, model_id, logical_model_name, '{to}', date, input_tokens,
                               output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                               estimated_cost_usd
                          FROM totals
                        ON CONFLICT (client_id, model_id, logical_model_name, provider_kind, date)
                        DO UPDATE SET
                            input_tokens        = client_token_usage_samples.input_tokens        + EXCLUDED.input_tokens,
                            output_tokens       = client_token_usage_samples.output_tokens       + EXCLUDED.output_tokens,
                            cached_input_tokens = client_token_usage_samples.cached_input_tokens + EXCLUDED.cached_input_tokens,
                            cache_write_tokens  = client_token_usage_samples.cache_write_tokens  + EXCLUDED.cache_write_tokens,
                            reasoning_tokens    = client_token_usage_samples.reasoning_tokens    + EXCLUDED.reasoning_tokens,
                            estimated_cost_usd  = CASE
                                                      WHEN client_token_usage_samples.estimated_cost_usd IS NULL
                                                           AND EXCLUDED.estimated_cost_usd IS NULL THEN NULL
                                                      ELSE COALESCE(client_token_usage_samples.estimated_cost_usd, 0)
                                                           + COALESCE(EXCLUDED.estimated_cost_usd, 0)
                                                  END;
                        """;
            }
        }

        /// <summary>Moves the Anthropic family's rows onto the identity and modes it declares as an add-in.</summary>
        private static class Anthropic
        {
            private const string LegacyIdentity = "Anthropic";
            private const string DeclaredIdentity = "meisterdev/anthropic";

            private const string DeclaredQualifier = DeclaredIdentity + ":";
            private const string LegacyQualifier = "";

            public static void Forward(MigrationBuilder migrationBuilder)
            {
                // The vocabulary first, while the rows that carry it can still be found by the identity they hold.
                Rewrite(migrationBuilder, LegacyIdentity, LegacyQualifier, DeclaredQualifier);
                migrationBuilder.Sql(Identity(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(LegacyIdentity, DeclaredIdentity));
                migrationBuilder.Sql(TenantAllowList(LegacyIdentity, DeclaredIdentity));
            }

            public static void Back(MigrationBuilder migrationBuilder)
            {
                Rewrite(migrationBuilder, DeclaredIdentity, DeclaredQualifier, LegacyQualifier);
                migrationBuilder.Sql(Identity(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(FoldedUsageSamples(DeclaredIdentity, LegacyIdentity));
                migrationBuilder.Sql(TenantAllowList(DeclaredIdentity, LegacyIdentity));
            }

            private static void Rewrite(
                MigrationBuilder migrationBuilder,
                string storedIdentity,
                string fromQualifier,
                string toQualifier)
            {
                migrationBuilder.Sql(CredentialShapes(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(BoundWireShape(storedIdentity, fromQualifier, toQualifier));
                migrationBuilder.Sql(ListedWireShapes(storedIdentity, fromQualifier, toQualifier));
            }

            // The one authentication mode the family declares. Two spellings arrive here — the plain key and the one
            // naming the header it travels in — and both become that one shape, because they always described the
            // same single value and the same request. A connection holding anything else is left alone: the value
            // names no shape this family serves, and the read path reports it rather than guessing.
            private static string CredentialShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET auth_mode = '{toQualifier}ApiKey'
                         WHERE provider_kind = '{storedIdentity}'
                           AND auth_mode IN ('{fromQualifier}XApiKey', '{fromQualifier}ApiKey');
                        """;
            }

            private static string BoundWireShape(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_purpose_bindings
                           SET protocol_mode = '{toQualifier}AnthropicMessages'
                         WHERE protocol_mode = '{fromQualifier}AnthropicMessages'
                           AND connection_profile_id IN (
                               SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}');
                        """;
            }

            // The listed shapes are a JSON array, rewritten element by element so the order an operator sees is the
            // order that comes back.
            private static string ListedWireShapes(string storedIdentity, string fromQualifier, string toQualifier)
            {
                return $"""
                        UPDATE ai_configured_models
                           SET supported_protocol_modes = (
                                   SELECT jsonb_agg(
                                              CASE listed.mode
                                                  WHEN '{fromQualifier}AnthropicMessages'
                                                      THEN to_jsonb('{toQualifier}AnthropicMessages'::text)
                                                  ELSE to_jsonb(listed.mode)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(supported_protocol_modes)
                                          WITH ORDINALITY AS listed(mode, ordinal))
                         WHERE connection_profile_id IN (
                                   SELECT id FROM ai_connection_profiles WHERE provider_kind = '{storedIdentity}')
                           AND supported_protocol_modes @> to_jsonb('{fromQualifier}AnthropicMessages'::text);
                        """;
            }

            private static string Identity(string from, string to)
            {
                return $"""
                        UPDATE ai_connection_profiles
                           SET provider_kind = '{to}'
                         WHERE provider_kind = '{from}';
                        """;
            }

            private static string TenantAllowList(string from, string to)
            {
                return $"""
                        UPDATE tenants
                           SET allowed_ai_provider_kinds = (
                                   SELECT jsonb_agg(
                                              CASE listed.entry
                                                  WHEN '{from}' THEN to_jsonb('{to}'::text)
                                                  ELSE to_jsonb(listed.entry)
                                              END
                                              ORDER BY listed.ordinal)
                                     FROM jsonb_array_elements_text(allowed_ai_provider_kinds)
                                          WITH ORDINALITY AS listed(entry, ordinal))
                         WHERE allowed_ai_provider_kinds @> to_jsonb('{from}'::text);
                        """;
            }

            // Summed into the target row and deleted, rather than updated in place: the identity is part of the
            // unique index, so an update onto an identity a row already holds for the same client, model, logical
            // model and day violates it and takes the whole migration down with it.
            private static string FoldedUsageSamples(string from, string to)
            {
                return $"""
                        WITH moved AS (
                            DELETE FROM client_token_usage_samples
                             WHERE provider_kind = '{from}'
                            RETURNING client_id, model_id, logical_model_name, date, input_tokens, output_tokens,
                                      cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd
                        ), totals AS (
                            SELECT client_id,
                                   model_id,
                                   logical_model_name,
                                   date,
                                   SUM(input_tokens) AS input_tokens,
                                   SUM(output_tokens) AS output_tokens,
                                   SUM(cached_input_tokens) AS cached_input_tokens,
                                   SUM(cache_write_tokens) AS cache_write_tokens,
                                   SUM(reasoning_tokens) AS reasoning_tokens,
                                   SUM(estimated_cost_usd) AS estimated_cost_usd
                              FROM moved
                             GROUP BY client_id, model_id, logical_model_name, date
                        )
                        INSERT INTO client_token_usage_samples
                            (id, client_id, model_id, logical_model_name, provider_kind, date, input_tokens,
                             output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                             estimated_cost_usd)
                        SELECT gen_random_uuid(), client_id, model_id, logical_model_name, '{to}', date, input_tokens,
                               output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens,
                               estimated_cost_usd
                          FROM totals
                        ON CONFLICT (client_id, model_id, logical_model_name, provider_kind, date)
                        DO UPDATE SET
                            input_tokens        = client_token_usage_samples.input_tokens        + EXCLUDED.input_tokens,
                            output_tokens       = client_token_usage_samples.output_tokens       + EXCLUDED.output_tokens,
                            cached_input_tokens = client_token_usage_samples.cached_input_tokens + EXCLUDED.cached_input_tokens,
                            cache_write_tokens  = client_token_usage_samples.cache_write_tokens  + EXCLUDED.cache_write_tokens,
                            reasoning_tokens    = client_token_usage_samples.reasoning_tokens    + EXCLUDED.reasoning_tokens,
                            estimated_cost_usd  = CASE
                                                      WHEN client_token_usage_samples.estimated_cost_usd IS NULL
                                                           AND EXCLUDED.estimated_cost_usd IS NULL THEN NULL
                                                      ELSE COALESCE(client_token_usage_samples.estimated_cost_usd, 0)
                                                           + COALESCE(EXCLUDED.estimated_cost_usd, 0)
                                                  END;
                        """;
            }
        }
    }
}
