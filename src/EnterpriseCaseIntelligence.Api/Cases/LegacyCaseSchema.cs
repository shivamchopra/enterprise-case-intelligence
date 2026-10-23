namespace EnterpriseCaseIntelligence.Api.Cases;

// Frozen versions 1–3: exact validation of shipped files, never new-store DDL.
internal static class LegacyCaseSchema
{
    internal const string Cases = """
        CREATE TABLE cases (
            id TEXT NOT NULL PRIMARY KEY,
            title TEXT NOT NULL,
            description TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('New', 'InReview', 'Closed'))
        ) STRICT
        """;

    internal const string History = """
        CREATE TABLE case_status_history (
            case_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence BETWEEN 1 AND 3),
            kind TEXT NOT NULL CHECK (kind IN ('Created', 'Baseline', 'StatusChanged')),
            from_status TEXT CHECK (from_status IS NULL OR from_status IN ('New', 'InReview', 'Closed')),
            to_status TEXT NOT NULL CHECK (to_status IN ('New', 'InReview', 'Closed')),
            recorded_at_utc TEXT NOT NULL,
            PRIMARY KEY (case_id, sequence),
            FOREIGN KEY (case_id) REFERENCES cases(id),
            CHECK (
                (sequence = 1 AND kind = 'Created' AND from_status IS NULL AND to_status = 'New')
                OR (sequence = 1 AND kind = 'Baseline' AND from_status IS NULL)
                OR (sequence BETWEEN 2 AND 3 AND kind = 'StatusChanged' AND from_status IS NOT NULL
                    AND ((from_status = 'New' AND to_status = 'InReview')
                        OR (from_status = 'InReview' AND to_status = 'Closed')))
            )
        ) STRICT
        """;

    internal const string CreationKeys = """
        CREATE TABLE case_creation_keys (
            request_key TEXT NOT NULL PRIMARY KEY,
            case_id TEXT NOT NULL UNIQUE,
            FOREIGN KEY (case_id) REFERENCES cases(id),
            CHECK (
                length(request_key) = 36
                AND instr(request_key, char(0)) = 0
                AND substr(request_key, 9, 1) = '-'
                AND substr(request_key, 14, 1) = '-'
                AND substr(request_key, 19, 1) = '-'
                AND substr(request_key, 24, 1) = '-'
                AND length(replace(request_key, '-', '')) = 32
                AND replace(request_key, '-', '') NOT GLOB '*[^0-9a-f]*'
                AND request_key <> '00000000-0000-0000-0000-000000000000'
            )
        ) STRICT
        """;
}
