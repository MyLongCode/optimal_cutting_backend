import { Button, Card, Flex, Typography } from 'antd';
import { Cutting2DForm } from '../../components/forms/Cutting2DForm/Cutting2DForm';
import { useAppSelector } from '../../app/hooks';
import { selectCalculateData2D } from '../../features/cutting2DSlice';
import { Cutting2DNestingResult } from '../../types/Nesting2D';
import styles from './Cutting2D.module.css';

const DEFAULT_PREVIEW_SIZE = 1000;

const downloadTextFile = (content: string, filename: string, type: string) => {
    const blob = new Blob([content], { type });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(url);
};

const getPreviewSize = (cuttingData: Cutting2DNestingResult) => {
    const firstWorkpiece = cuttingData.workpieces[0];

    return {
        width: Math.max(firstWorkpiece?.width ?? DEFAULT_PREVIEW_SIZE, 1),
        height: Math.max(firstWorkpiece?.height ?? DEFAULT_PREVIEW_SIZE, 1),
    };
};

const withSvgViewBox = (svg: string, cuttingData: Cutting2DNestingResult) => {
    if (!svg || svg.includes('viewBox=')) return svg;

    const { width, height } = getPreviewSize(cuttingData);
    return svg.replace(
        '<svg ',
        `<svg width="${width}" height="${height}" viewBox="0 -${height} ${width} ${height}" `
    );
};

export const Cutting2D = () => {
    const cuttingData = useAppSelector(selectCalculateData2D);
    const previewSvg = withSvgViewBox(cuttingData.svg, cuttingData);
    const hasResult = previewSvg.length > 0 || cuttingData.placedParts.length > 0;

    return (
        <Flex className={styles.cutting2D} gap={24}>
            <Cutting2DForm />
            <Flex vertical className={styles.cutting2D__result} gap={16}>
                {hasResult ? (
                    <>
                        <Card className={styles.cutting2D__summary}>
                            <Flex gap={24} wrap='wrap'>
                                <Typography.Text>
                                    Размещено: {cuttingData.placedParts.length}
                                </Typography.Text>
                                <Typography.Text>
                                    Не размещено: {cuttingData.unplacedParts.length}
                                </Typography.Text>
                                <Typography.Text>
                                    Использование: {cuttingData.totalUtilization.toFixed(2)}%
                                </Typography.Text>
                            </Flex>
                            {cuttingData.unplacedParts.length > 0 && (
                                <Typography.Text type='warning'>
                                    Не удалось разместить детали:{' '}
                                    {cuttingData.unplacedParts.join(', ')}
                                </Typography.Text>
                            )}
                        </Card>
                        <Flex gap={12}>
                            <Button
                                disabled={!previewSvg}
                                onClick={() =>
                                    downloadTextFile(
                                        previewSvg,
                                        'cutting-2d.svg',
                                        'image/svg+xml'
                                    )
                                }
                            >
                                Скачать SVG
                            </Button>
                            <Button
                                disabled={!cuttingData.dxf}
                                onClick={() =>
                                    downloadTextFile(
                                        cuttingData.dxf,
                                        'cutting-2d.dxf',
                                        'application/dxf'
                                    )
                                }
                            >
                                Скачать DXF
                            </Button>
                        </Flex>
                        {previewSvg && (
                            <div
                                className={styles.cutting2D__svg}
                                dangerouslySetInnerHTML={{ __html: previewSvg }}
                            />
                        )}
                    </>
                ) : (
                    <Flex className={styles.cutting2D__empty} align='center' justify='center'>
                        Выберите детали и заготовку, чтобы построить схему 2D-раскроя.
                    </Flex>
                )}
            </Flex>
        </Flex>
    );
};
