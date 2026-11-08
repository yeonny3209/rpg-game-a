-- 단계 1 초기 스키마. 계정 → 캐릭터 구조(결정 Q4), 나중 기능용 테이블(유일 슬롯, 감사 로그)도 미리 만든다.

create table accounts (
    id            bigserial primary key,
    login_name    text not null,
    created_at    timestamptz not null default now(),
    last_login_at timestamptz
);
create unique index accounts_login_name_uq on accounts (lower(login_name));

create table characters (
    id               bigserial primary key,
    account_id       bigint not null references accounts(id),
    slot             smallint not null default 0,          -- 캐릭터 슬롯 3개(5권 11장) 중 몇 번째
    name             text not null,
    level            int not null default 1,
    exp              bigint not null default 0,
    gold             bigint not null default 0 check (gold >= 0),
    stat_points      int not null default 0,
    str              int not null default 10,
    dex              int not null default 10,
    int_             int not null default 10,
    con              int not null default 10,
    spi              int not null default 10,
    hp               int not null,
    zone             text not null,
    has_position     boolean not null default false,
    pos_x            real not null default 0,
    pos_y            real not null default 0,
    pos_z            real not null default 0,
    yaw              real not null default 0,
    unlocks          int not null default 0,               -- UnlockFlags 비트
    last_level_up_at timestamptz not null default now(),   -- 용기사 유지 조건(3권)용
    created_at       timestamptz not null default now(),
    updated_at       timestamptz not null default now(),
    unique (account_id, slot)
);
create unique index characters_name_uq on characters (lower(name));

-- 가방·장비. 같은 (캐릭터, 보관함, 칸)에는 한 묶음만.
create table character_items (
    character_id bigint not null references characters(id) on delete cascade,
    container    smallint not null,     -- 0 가방, 1 장비 (창고는 나중에)
    slot         smallint not null,
    item_id      text not null,
    count        int not null check (count > 0),
    primary key (character_id, container, slot)
);

create table quest_progress (
    character_id bigint not null references characters(id) on delete cascade,
    quest_id     text not null,
    state        smallint not null,     -- 1 진행 중, 3 완료
    counters     int[] not null default '{}',
    updated_at   timestamptz not null default now(),
    primary key (character_id, quest_id)
);

-- 서버 전체에서 한 명만 가질 수 있는 것(드래곤 계약, 공허의 힘, 시련 선점). 조건부 UPDATE로 원자적으로 잡는다(기술설계 2.4).
create table unique_slots (
    slot_key            text primary key,
    holder_character_id bigint references characters(id),
    acquired_at         timestamptz,
    version             bigint not null default 0
);

-- 중요한 변화 기록(퀘스트 완료, 레벨업 등). 나중에 거래·경매·강화도 여기에.
create table audit_log (
    id           bigserial primary key,
    at           timestamptz not null default now(),
    character_id bigint,
    kind         text not null,
    data         jsonb
);
create index audit_log_character_idx on audit_log (character_id, at);
