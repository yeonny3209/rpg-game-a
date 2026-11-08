-- 단계 2a: 직업, 직업 고유 성장(훈련량), 스킬 숙련도.
alter table characters add column job text not null default '';
alter table characters add column job_tier smallint not null default 0;
alter table characters add column training bigint not null default 0;          -- 동양 검사 훈련량(누적)
alter table characters add column training_today int not null default 0;       -- 오늘 수련장에서 쌓은 양
alter table characters add column training_day int not null default 0;         -- 위 값이 어느 날짜(yyyymmdd)의 것인지

create table character_skills (
    character_id bigint not null references characters(id) on delete cascade,
    skill_id     text not null,
    level        smallint not null default 1,
    uses         int not null default 0,
    primary key (character_id, skill_id)
);
