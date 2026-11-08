-- 단계 3a: 아이템 등급·랜덤 옵션, 가방 확장.
alter table character_items add column grade smallint not null default 0;
alter table character_items add column opts text not null default '';
alter table characters add column bag_slots int not null default 0;
