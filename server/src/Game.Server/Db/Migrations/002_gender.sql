-- 단계 2a: 캐릭터 모습(성별). 0 = 아직 안 골랐음(첫 접속 직후 선택창), 1 = 남자, 2 = 여자.
alter table characters add column gender smallint not null default 0;
