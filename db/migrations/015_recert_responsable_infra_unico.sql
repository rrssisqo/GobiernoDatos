/* =====================================================================
   Migración 015 · Un solo responsable de infraestructura para todas las macro áreas
   Como el gerente (migración 012): se configura una vez en Recertificación → Macro áreas. Al asignar las bajas de una
   macro área solo se captura el número de cambio; gd.recert_asignacion_infra guarda el responsable vigente en ese momento.
   Requiere 014. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   ===================================================================== */
IF OBJECT_ID('gd.recert_responsable_infra') IS NULL
CREATE TABLE gd.recert_responsable_infra (
    responsable_id TINYINT NOT NULL CONSTRAINT pk_recert_responsable_infra PRIMARY KEY CONSTRAINT ck_rri_unico CHECK (responsable_id = 1),
    usuario_red    NVARCHAR(100) NOT NULL,
    nombre         NVARCHAR(150) NOT NULL,
    correo         NVARCHAR(254) NOT NULL,
    usuario        NVARCHAR(100) NOT NULL CONSTRAINT df_rri_usuario DEFAULT (SUSER_SNAME()),
    fecha_registro DATETIME2(0) NOT NULL CONSTRAINT df_rri_fecha DEFAULT (SYSDATETIME())
);
GO

-- Si ya había asignaciones, el responsable de la más reciente queda como el responsable único.
INSERT INTO gd.recert_responsable_infra (responsable_id, usuario_red, nombre, correo, usuario)
SELECT TOP (1) 1, responsable_usuario_red, responsable_nombre, responsable_correo, asignado_por
FROM gd.recert_asignacion_infra
WHERE NOT EXISTS (SELECT 1 FROM gd.recert_responsable_infra)
ORDER BY asignacion_id DESC;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '015_recert_responsable_infra_unico')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('015_recert_responsable_infra_unico', N'Un solo responsable de infraestructura para las bajas de todas las macro áreas');
GO
