create table request (
                         id uuid primary key,
                         arrived_at timestamp not null default now(),
                         source text,
                         sink text,
                         target_flow numeric,
                         to_tokens text[],
                         from_tokens text[],
                         exclude_to_tokens text[],
                         exclude_from_tokens text[],
                         with_wrap boolean
);

create index idx_requests_arrived_at on request (arrived_at);
create index idx_requests_source on request (source);
create index idx_requests_sink on request (sink);

create table response (
                          id uuid primary key,
                          request_id uuid references request(id),
                          arrived_at timestamp not null default now(),
                          actual_flow numeric,
                          success boolean,
                          error_message text,
                          result jsonb
);

create index idx_responses_arrived_at on response (arrived_at);
create index idx_responses_success on response (success);