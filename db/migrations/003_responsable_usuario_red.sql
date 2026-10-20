/* =====================================================================
   Migración 003 · Usuario de red del responsable
   El portal reconoce al certificador por su usuario de red (DOMINIO\usuario, autenticación de Windows),
   no por su nombre. Requiere 001. Idempotente. Ejecutar con QUOTED_IDENTIFIER ON (sqlcmd -I).
   Los responsables existentes quedan sin usuario de red; se les asigna al darlos de alta como certificadores.
   ===================================================================== */
IF OBJECT_ID('gd.migracion') IS NULL OR NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '001_v3_recertificacion')
    THROW 50000, N'Aplica primero db/migrations/001_v3_recertificacion.sql.', 1;
GO

-- Forma canónica: dominio en mayúsculas, usuario en minúsculas (MABENET\rrsmexti).
IF COL_LENGTH('gd.responsable', 'usuario_red') IS NULL
    ALTER TABLE gd.responsable ADD usuario_red NVARCHAR(100) NULL;
GO

IF INDEXPROPERTY(OBJECT_ID('gd.responsable'), 'ux_responsable_usuario_red', 'IndexID') IS NULL
    CREATE UNIQUE INDEX ux_responsable_usuario_red ON gd.responsable (usuario_red) WHERE usuario_red IS NOT NULL;
GO

-- A quién recordar: ahora con el usuario de red del certificador.
CREATE OR ALTER VIEW gd.vw_recert_pendientes_certificador
AS
SELECT  am.evento_id, am.evento, am.macro_area_id, am.macro_area, c.tipo,
        r.responsable_id, r.nombre AS certificador, r.usuario_red, r.correo, am.pendientes
FROM gd.vw_recert_avance_macro_area AS am
JOIN gd.recert_evento AS e ON e.evento_id = am.evento_id AND e.estatus = 'ABIERTO'
JOIN gd.macro_area_certificador AS c ON c.macro_area_id = am.macro_area_id
     AND c.fecha_desde <= CAST(GETDATE() AS DATE) AND (c.fecha_hasta IS NULL OR c.fecha_hasta >= CAST(GETDATE() AS DATE))
JOIN gd.responsable AS r ON r.responsable_id = c.responsable_id
WHERE am.pendientes > 0;
GO

IF NOT EXISTS (SELECT 1 FROM gd.migracion WHERE migracion_id = '003_responsable_usuario_red')
    INSERT INTO gd.migracion (migracion_id, descripcion)
    VALUES ('003_responsable_usuario_red', N'Usuario de red del responsable: identifica al certificador (autenticación de Windows)');
GO
