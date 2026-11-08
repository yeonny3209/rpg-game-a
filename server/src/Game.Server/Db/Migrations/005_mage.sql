-- 단계 2b: 마법사(마나·속성·서클). mana -1 = 가득(처음 접속 때 최대 마나로 채움).
alter table characters add column mana int not null default -1;
alter table characters add column element text not null default '';
alter table characters add column circle smallint not null default 0;
